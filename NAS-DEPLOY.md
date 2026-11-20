# Running your own Hallow Assist relay (NAS + Cloudflare Tunnel)

Hallow Assist connects the two PCs through a small **relay** that you run yourself. The relay only pairs the two ends
by their 9-digit code and passes their encrypted messages along: it can't see screens, keys, chat or files, and it
stores nothing. It also serves the browser viewer, so a helper can use a phone, tablet, TV or any PC browser.

What you need:

- A machine that runs Docker - most NAS boxes (Synology, UGREEN UGOS, QNAP, Unraid), a mini PC or a Raspberry Pi.
  The relay uses about 30 MB of memory.
- An `https://` address that works from anywhere. The easy way is a **Cloudflare Tunnel** (free), the same kind you may
  already use for other self-hosted apps. A reverse proxy with a certificate (Caddy, Nginx Proxy Manager, Traefik) works too -
  it must pass WebSocket upgrades on `/ws`.

## 1. Start the relay on the NAS

Copy the `server` folder from the release zip (or this repo) to the NAS, for example to `/volume1/docker/hallow-assist`,
then in that folder:

```
docker compose up -d --build
```

Check it from your PC: `http://<NAS address>:8090/healthz` should show `{"ok":true,...}`.
Port 8090 on the NAS is only for your home network and for the tunnel - don't forward it on your router.

### Or: from your PC with the deploy script

If you can already sign in to the NAS with an SSH key, `server/scripts/deploy-nas.mjs` does the copying and the
`docker compose up` for you. Create `server/deploy.env` (it's ignored by git):

```
NAS_HOST=<NAS address on your home network, e.g. 192.168.1.20>
NAS_USER=<your SSH account on the NAS>
NAS_DIR=/volume1/docker/hallow-assist
SSH_KEY=<path to the private key you use for the NAS>
PUBLIC_URL=https://assist.<your domain>
# DOCKER=sudo docker     (only if docker needs sudo on your NAS)
```

Then `node server/scripts/deploy-nas.mjs --dry-run` shows what it would do, and `node server/scripts/deploy-nas.mjs`
does it. It only writes into `NAS_DIR`, never overwrites the `.env` there, and never touches the tunnel, DNS or other
containers. Run it again for each new relay version.

## 2. Give it an https address (Cloudflare Tunnel)

Pick a hostname, e.g. `assist.<your domain>`.

**If your tunnel is managed in the Cloudflare dashboard** (Zero Trust > Networks > Tunnels > your tunnel >
Public Hostname > Add): subdomain `assist`, your domain, service type `HTTP`, URL `<NAS address>:8090`. Cloudflare adds
the DNS record for you. Done.

**If your tunnel uses a local `config.yml`** (a `cloudflared` container with `tunnel:` and `credentials-file:` lines):

1. Add a rule **above** the last catch-all line (`- service: http_status:404`):

   ```yaml
     - hostname: assist.<your domain>
       service: http://<NAS address>:8090
   ```

   (If the relay container shares a Docker network with `cloudflared`, `http://hallow-assist:8080` works too.)
2. Add the DNS record: either `cloudflared tunnel route dns <tunnel name or id> assist.<your domain>`, or in the Cloudflare
   dashboard > DNS > Add record: type `CNAME`, name `assist`, target `<tunnel id>.cfargotunnel.com`, Proxied (orange cloud).
3. Put the updated `config.yml` where your `cloudflared` reads it and restart that container.

WebSockets work through Cloudflare Tunnels without any extra setting. Don't put Cloudflare Access (a login page) in
front of the relay: the Windows app can't get past it. The relay has its own protection: random one-time codes that expire,
a rate limit on wrong codes per visitor, and the end-to-end password check.

Check: `https://assist.<your domain>/healthz` answers, and `https://assist.<your domain>/` shows the Hallow Assist page.

Finally set `PUBLIC_URL=https://assist.<your domain>` in the `.env` file next to `docker-compose.yml` on the NAS and run
`docker compose up -d` again (the deploy script writes it the first time if you set it in `deploy.env`).

## 3. Use it

- In Hallow Assist on every PC (yours and theirs): Settings > Relay address > `https://assist.<your domain>` > Save.
- They click **Get help** and read you the code and password. You type them under **Help someone** - or open
  `https://assist.<your domain>` on your phone, tablet or TV browser and type them there.

## Settings (environment variables)

| Variable | Default | What it does |
|---|---|---|
| `PORT` | `8080` | Port inside the container (compose maps it to 8090 on the NAS) |
| `PUBLIC_URL` | empty | The relay's https address, shown on its page |
| `TRUST_PROXY` | `cloudflare` in compose, `none` otherwise | Use `CF-Connecting-IP` for per-visitor limits - trusted only from Docker networks / this machine |
| `CODE_TTL_SECONDS` | `600` | An unused code stops working after this |
| `JOIN_FAILS_PER_10_MIN` | `10` | Wrong codes from one visitor before they must wait |
| `JOINS_PER_10_MIN` | `40` | Join attempts from one visitor in 10 minutes |
| `MAX_MESSAGE_BYTES` | `4194304` | Largest single message passed along |
| `IDLE_TIMEOUT_SECONDS` | `60` | Silent connections are dropped |
| `MAX_SESSIONS` | `200` | Codes open at the same time |
| `MAX_CONNECTIONS_PER_IP` | `20` | Open connections per visitor |
| `LOG` | on | `off` = no log lines. Codes, passwords and payloads are never logged either way |

## Updating

Copy the new `server` folder over the old one (your `.env` stays) and run `docker compose up -d --build`, or run the
deploy script again.
