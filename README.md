# Jellyfin DeoVR Plugin

Watch your Jellyfin library in [DeoVR](https://deovr.com): regular movies on a 2D cinema screen, 3D movies (SBS / TB) on a 3D cinema screen, and VR180 / VR360 videos in their native projection.

| Content | screenType | stereoMode | Shown in DeoVR as |
|---|---|---|---|
| Regular movie | `flat` | `off` | 2D cinema screen |
| 3D side-by-side (HSBS / FSBS) | `flat` | `sbs` | 3D cinema screen |
| 3D top-bottom (HTAB / FTAB) | `flat` | `tb` | 3D cinema screen |
| VR180 | `dome` | `sbs` | 180° VR |
| VR360 | `sphere` | `off` / `tb` / `sbs` | 360° VR |

- Requires Jellyfin **12.4** or newer (.NET 10).
- Direct play only: the original file is streamed with HTTP range support, no transcoding. DeoVR must be able to decode the file itself.

## Installation

### From the plugin repository
1. Dashboard → Plugins → Repositories → **+**
2. Repository URL:
   ```
   https://raw.githubusercontent.com/MrYoshii/jellyvr/main/manifest.json
   ```
3. Catalog → **DeoVR** → Install, then restart Jellyfin.

### Manual
Download `deovr_<version>.zip` from the releases page and extract it into the Jellyfin plugin directory:

| Install type | Plugin directory |
|---|---|
| Linux package | `/var/lib/jellyfin/plugins/DeoVR_<version>/` |
| Docker | `/config/plugins/DeoVR_<version>/` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins\DeoVR_<version>\` |

Restart Jellyfin afterwards.

## Setup
1. Dashboard → Plugins → DeoVR.
2. Set **Public base URL** to the address DeoVR uses to reach your server (e.g. `https://jellyfin.example.com`). Strongly recommended behind a reverse proxy.
3. Copy the **feed link** of a user and open it in the DeoVR browser. Bookmark it there.

DeoVR needs a valid HTTPS certificate for remote servers.

## Access methods
| Method | URL | Notes |
|---|---|---|
| Feed link (recommended) | `/deovr/f/<key>` | Per-user key, shown on the plugin page (admins only). |
| Login | `/deovr` | DeoVR sends `login` / `password`; checked against Jellyfin users. |
| Anonymous | `/deovr` | Optional fallback user configured on the plugin page. Trusted networks only. |

## 3D / VR detection
Priority, first match wins:

1. **Item tags** (set in Jellyfin's metadata editor): `deovr-flat`, `deovr-180`, `deovr-360`, `deovr-fisheye`, `deovr-mkx200`, `deovr-sbs`, `deovr-tb`, `deovr-2d`
2. **Jellyfin's 3D format**, detected from the file name, e.g. `Avatar (2009).3D.HSBS.mkv`, `Movie (2012).3D.HTAB.mkv`
3. **File name tokens**:
   - `sbs`, `hsbs`, `fsbs`, `3dh` → side-by-side
   - `tab`, `htab`, `ftab`, `3dv` → top-bottom
   - `vr180`, or `180` together with `lr` / `sbs` / `vr` → 180°
   - `vr360`, or `360` together with a VR hint → 360°

MVC 3D (Blu-ray frame packing) is not supported by DeoVR and plays as 2D.

## Security
- No Jellyfin API keys or session tokens in URLs. Video and stream URLs are **HMAC-SHA256 signed and expire**: library entries after the configured link lifetime (default 24 h), stream links after the video runtime + 2 h.
- A signed link grants access to exactly one file for one user. It gives no access to the Jellyfin API.
- Feed keys are 80-bit HMAC values derived from the signing secret and the user ID.
- On every request the plugin enforces the user's Jellyfin policy: disabled users, *Allow media playback*, parental access schedule, *Allow remote connections* (otherwise LAN only). Single items are checked with Jellyfin's full visibility check (library access and parental ratings, including inherited ones).
- File paths come from the Jellyfin database only, never from the request.
- Building a feed does bounded work: sorting and the item limit are applied in the library query, and no per-file disk access happens until a video is played.
- The signing secret is always at least 32 characters; a missing or shorter value is replaced by a random one.
- `/deovr/admin/links` requires an administrator.
- Logins go through Jellyfin's own `AuthenticateUser`.

Known limitations:
- Anyone who has a feed link or a (not yet expired) stream link can use it. Links are not bound to an IP or device. This is inherent to URL-based players like DeoVR, which cannot send auth headers.
- Feed keys and signatures are part of the URL and therefore appear in reverse proxy access logs.
- Feed keys cannot be revoked per user. Changing the signing secret revokes all of them.
- The plugin has no own rate limiting. Limit `/deovr` at your reverse proxy if exposed to the internet.
- The remote-access check needs the real client IP. Behind a reverse proxy, add it under Dashboard → Networking → Known proxies.
- Without a public base URL, links are built from the request's `Host` header.

## Endpoints
| Route | Purpose |
|---|---|
| `GET/POST /deovr` | Library (login / anonymous) |
| `GET/POST /deovr/f/{key}` | Library via feed key |
| `GET/POST /deovr/v/{id}?u&e&s` | Single video JSON (signed, expires) |
| `GET/HEAD /deovr/s/{id}?u&e&s` | File stream with range support (signed, expires) |
| `GET /deovr/admin/links` | Feed links of all users (admin only) |

## Building
```bash
./build.sh        # Linux/macOS, uses Docker
build.cmd         # Windows, uses a local .NET 10 SDK or Docker
```
Output: `dist/DeoVR_1.0.0.0/Jellyfin.Plugin.DeoVR.dll`

## Releasing
```bash
git tag v1.0.0
git push origin v1.0.0
```
GitHub Actions builds the plugin, attaches `deovr_<version>.zip` (plus a `.sha256` file) to the release and updates `manifest.json` on `main`. Only tags of the form `vX.Y.Z` on commits contained in `main` are released.

## License
GPL-3.0
