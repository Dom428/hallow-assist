// Hallow Assist end-to-end encryption, Windows side (the browser has the same in proto.js with WebCrypto):
// ECDH P-256 with Windows CNG, bound to the one-time password (PBKDF2) and the session code via HKDF, a key-confirmation
// step, then AES-256-GCM per message (Windows BCrypt) with counters that must go up by exactly one (replays are refused).
// The relay passes these bytes along but can't read or change them. See proto.js for the exact byte layout.
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HallowAssist
{
    public static class Kdf
    {
        public const int Iterations = 100000;
        static readonly Encoding U = new UTF8Encoding(false);
        public static byte[] Utf8(string s) { return U.GetBytes(s); }
        public static byte[] Cat(params byte[][] parts) { int n = 0; foreach (var p in parts) n += p.Length; var o = new byte[n]; int i = 0; foreach (var p in parts) { Buffer.BlockCopy(p, 0, o, i, p.Length); i += p.Length; } return o; }
        public static byte[] Slice(byte[] b, int off, int len) { var o = new byte[len]; Buffer.BlockCopy(b, off, o, 0, len); return o; }
        public static byte[] Sha256(byte[] b) { using (var h = SHA256.Create()) return h.ComputeHash(b); }
        public static byte[] Hmac(byte[] key, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(data); }
        public static bool Equal(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0; }

        // one-time passwords: 6 letters/digits, case and spaces don't matter; longer (unattended) passwords are exact
        public static string NormPassword(string pw)
        {
            var s = (pw ?? "").Trim(); var compact = s.Replace(" ", "").Replace("-", "").Replace("\t", "");
            if (compact.Length == 6) { bool ok = true; foreach (var c in compact) if (!(c < 128 && char.IsLetterOrDigit(c))) ok = false; if (ok) return compact.ToUpperInvariant(); }
            return s;
        }
        // v1.1: a one-time password (6 letters/digits) - its code changes every time, so there's nothing to save
        public static bool IsOneTime(string pw)
        {
            var c = (pw ?? "").Trim().Replace(" ", "").Replace("-", "").Replace("\t", "");
            if (c.Length != 6) return false; foreach (var ch in c) if (!(ch < 128 && char.IsLetterOrDigit(ch))) return false; return true;
        }
        public static string NormCode(string c) { var sb = new StringBuilder(); foreach (var ch in c ?? "") if (ch >= '0' && ch <= '9') sb.Append(ch); return sb.ToString(); }
        public static byte[] PasswordKey(string password, string code)
        {
            using (var p = new Rfc2898DeriveBytes(Utf8(NormPassword(password)), Utf8("hallow-assist/v1/pw/" + NormCode(code)), Iterations, HashAlgorithmName.SHA256)) return p.GetBytes(32);
        }
        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int len)
        {
            var prk = Hmac(salt, ikm); var okm = new byte[len]; var t = new byte[0]; int done = 0;
            for (byte i = 1; done < len; i++) { t = Hmac(prk, Cat(t, info, new[] { i })); int n = Math.Min(t.Length, len - done); Buffer.BlockCopy(t, 0, okm, done, n); done += n; }
            return okm;
        }
        // the 9-digit code of an unattended PC (the relay computes the same from the secret)
        public static string PersistentCode(string secretHex)
        {
            var h = Sha256(Utf8("hallow-assist/v1/persist/" + secretHex));
            ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | h[i];
            v >>= 4;   // the first 15 hex digits = 60 bits
            return (v % 900000000UL + 100000000UL).ToString();
        }
        public static string RandomHex(int bytes) { var b = new byte[bytes]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        // a one-time password: 6 characters without look-alikes (no 0/O, 1/I/L)
        public static string NewPassword()
        {
            const string A = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; var b = new byte[6]; var sb = new StringBuilder();
            using (var r = RandomNumberGenerator.Create()) for (int i = 0; i < 6; i++) { do r.GetBytes(b, 0, 1); while (b[0] >= 248); sb.Append(A[b[0] % 31]); }
            return sb.ToString();
        }
    }

    // ECDH P-256 key pair (CNG). Pub is the 65-byte uncompressed point WebCrypto uses.
    public sealed class EcdhKey : IDisposable
    {
        readonly ECDiffieHellmanCng ecdh;
        public readonly byte[] Pub;
        public EcdhKey()
        {
            ecdh = new ECDiffieHellmanCng(256) { KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash, HashAlgorithm = CngAlgorithm.Sha256 };
            var blob = ecdh.PublicKey.ToByteArray();   // BCRYPT_ECCPUBLIC_BLOB: "ECK1", 32, X, Y
            Pub = new byte[65]; Pub[0] = 4; Buffer.BlockCopy(blob, 8, Pub, 1, 64);
        }
        // SHA-256 of the shared x coordinate (what the browser computes as SHA256(deriveBits))
        public byte[] SharedHash(byte[] peerPub)
        {
            if (peerPub == null || peerPub.Length != 65 || peerPub[0] != 4) throw new CryptographicException("bad public key");
            var blob = new byte[72]; blob[0] = 0x45; blob[1] = 0x43; blob[2] = 0x4B; blob[3] = 0x31; blob[4] = 32;
            Buffer.BlockCopy(peerPub, 1, blob, 8, 64);
            using (var pk = ECDiffieHellmanCngPublicKey.FromByteArray(blob, CngKeyBlobFormat.EccPublicBlob)) return ecdh.DeriveKeyMaterial(pk);
        }
        public void Dispose() { ecdh.Dispose(); }
    }

    public class SessionKeys
    {
        public byte[] Th, KV2H, KH2V, MacV, MacH, ConfirmV, ConfirmH;
        public string Safety;
        public static SessionKeys Derive(byte[] zHash, byte[] pwKey, string code, byte[] viewerPub, byte[] hostPub, byte[] nonce)
        {
            var k = new SessionKeys();
            k.Th = Kdf.Sha256(Kdf.Cat(Kdf.Utf8("hallow-assist/v1"), Kdf.Utf8(Kdf.NormCode(code)), viewerPub, hostPub, nonce));
            var okm = Kdf.Hkdf(Kdf.Cat(zHash, pwKey), k.Th, Kdf.Utf8("hallow-assist/v1/keys"), 160);
            k.KV2H = Kdf.Slice(okm, 0, 32); k.KH2V = Kdf.Slice(okm, 32, 32); k.MacV = Kdf.Slice(okm, 64, 32); k.MacH = Kdf.Slice(okm, 96, 32);
            k.ConfirmV = Kdf.Hmac(k.MacV, Kdf.Cat(Kdf.Utf8("viewer"), k.Th));
            k.ConfirmH = Kdf.Hmac(k.MacH, Kdf.Cat(Kdf.Utf8("host"), k.Th));
            uint n = ((uint)okm[128] << 24) | ((uint)okm[129] << 16) | ((uint)okm[130] << 8) | okm[131];
            k.Safety = (n % 10000).ToString("D4");
            return k;
        }
    }

    // AES-256-GCM with Windows BCrypt (.NET Framework 4.8 has no AesGcm class)
    public sealed class AesGcm : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo
        {
            public int cbSize; public int dwInfoVersion; public IntPtr pbNonce; public int cbNonce; public IntPtr pbAuthData; public int cbAuthData;
            public IntPtr pbTag; public int cbTag; public IntPtr pbMacContext; public int cbMacContext; public int cbAAD; public long cbData; public int dwFlags;
        }
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern uint BCryptOpenAlgorithmProvider(out IntPtr h, string alg, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern uint BCryptSetProperty(IntPtr h, string prop, byte[] val, int len, int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objLen, byte[] secret, int len, int flags);
        [DllImport("bcrypt.dll")] static extern unsafe uint BCryptEncrypt(IntPtr key, byte* input, int inLen, ref AuthInfo info, IntPtr iv, int ivLen, byte* output, int outLen, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern unsafe uint BCryptDecrypt(IntPtr key, byte* input, int inLen, ref AuthInfo info, IntPtr iv, int ivLen, byte* output, int outLen, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern uint BCryptCloseAlgorithmProvider(IntPtr h, int flags);

        static readonly IntPtr alg;
        static AesGcm()
        {
            if (BCryptOpenAlgorithmProvider(out alg, "AES", null, 0) != 0) throw new CryptographicException("AES isn't available");
            var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            if (BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0) != 0) throw new CryptographicException("AES-GCM isn't available");
        }
        IntPtr key; readonly object gate = new object();
        public AesGcm(byte[] k)
        {
            if (k.Length != 32) throw new ArgumentException("AES-256 needs a 32-byte key");
            if (BCryptGenerateSymmetricKey(alg, out key, IntPtr.Zero, 0, k, k.Length, 0) != 0) throw new CryptographicException("couldn't make the key");
        }
        // returns ciphertext || 16-byte tag
        public unsafe byte[] Seal(byte[] nonce, byte[] plain, int pOff, int pLen, byte[] aad)
        {
            var output = new byte[pLen + 16];
            lock (gate)
                fixed (byte* n = nonce) fixed (byte* a = aad) fixed (byte* p = plain) fixed (byte* o = output)
                {
                    var info = Info(n, nonce.Length, a, aad.Length, o + pLen);
                    int got; var st = BCryptEncrypt(key, pLen == 0 ? null : p + pOff, pLen, ref info, IntPtr.Zero, 0, pLen == 0 ? null : o, pLen, out got, 0);
                    if (st != 0) throw new CryptographicException("encrypt failed 0x" + st.ToString("X8"));
                }
            return output;
        }
        // null if the message was changed (wrong tag)
        public unsafe byte[] Open(byte[] nonce, byte[] msg, int off, int len, byte[] aad)
        {
            if (len < 16) return null;
            int cl = len - 16; var output = new byte[cl];
            lock (gate)
                fixed (byte* n = nonce) fixed (byte* a = aad) fixed (byte* m = msg) fixed (byte* o = output)
                {
                    var info = Info(n, nonce.Length, a, aad.Length, m + off + cl);
                    int got; var st = BCryptDecrypt(key, cl == 0 ? null : m + off, cl, ref info, IntPtr.Zero, 0, cl == 0 ? null : o, cl, out got, 0);
                    if (st != 0) return null;
                }
            return output;
        }
        static unsafe AuthInfo Info(byte* n, int nl, byte* a, int al, byte* tag)
        {
            return new AuthInfo { cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1, pbNonce = (IntPtr)n, cbNonce = nl, pbAuthData = (IntPtr)a, cbAuthData = al, pbTag = (IntPtr)tag, cbTag = 16 };
        }
        public void Dispose() { lock (gate) { if (key != IntPtr.Zero) { BCryptDestroyKey(key); key = IntPtr.Zero; } } }
    }

    // A sealed two-way channel. 0x10 | counter(8, big-endian) | AES-GCM(iv = direction(4) | counter(8), aad = the 9-byte header)
    public sealed class Channel : IDisposable
    {
        public const byte Sealed = 0x10;
        readonly AesGcm send, recv; readonly uint sendDir, recvDir;
        long sendCtr, recvCtr; readonly object sendLock = new object(), recvLock = new object();
        public Channel(SessionKeys k, bool isHost)
        {
            send = new AesGcm(isHost ? k.KH2V : k.KV2H); recv = new AesGcm(isHost ? k.KV2H : k.KH2V);
            sendDir = isHost ? 2u : 1u; recvDir = isHost ? 1u : 2u;
        }
        static byte[] Iv(uint dir, long ctr) { var iv = new byte[12]; iv[0] = (byte)(dir >> 24); iv[1] = (byte)(dir >> 16); iv[2] = (byte)(dir >> 8); iv[3] = (byte)dir; for (int i = 0; i < 8; i++) iv[4 + i] = (byte)(ctr >> (56 - 8 * i)); return iv; }
        // callers that send must keep the order of Seal calls = the order on the wire (the relay sender queue does)
        public byte[] Seal(byte[] plain) { lock (sendLock) { var ctr = ++sendCtr; var head = new byte[9]; head[0] = Sealed; for (int i = 0; i < 8; i++) head[1 + i] = (byte)(ctr >> (56 - 8 * i)); return Kdf.Cat(head, send.Seal(Iv(sendDir, ctr), plain, 0, plain.Length, head)); } }
        // throws on a changed, replayed or out-of-order message
        public byte[] Open(byte[] msg)
        {
            lock (recvLock)
            {
                if (msg.Length < 25 || msg[0] != Sealed) throw new CryptographicException("not a sealed message");
                long ctr = 0; for (int i = 0; i < 8; i++) ctr = (ctr << 8) | msg[1 + i];
                if (ctr != recvCtr + 1) throw new CryptographicException(ctr <= recvCtr ? "replayed message" : "message out of order");
                var plain = recv.Open(Iv(recvDir, ctr), msg, 9, msg.Length - 9, Kdf.Slice(msg, 0, 9));
                if (plain == null) throw new CryptographicException("message was changed on the way");
                recvCtr = ctr; return plain;
            }
        }
        public void Dispose() { send.Dispose(); recv.Dispose(); }
    }
}
