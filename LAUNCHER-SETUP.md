# Ant's Modding Tools — Launcher / Microsoft sign-in setup

The launcher signs you into Minecraft with your **Microsoft account** so it can boot the game authenticated.
It does this the most secure, privacy-minded way there is:

- You sign in **on Microsoft's own website** in your normal browser — your password **never** touches this app.
- The only secret kept on disk is Microsoft's **refresh token**, and it's encrypted by your **operating system's
  secure vault** (Windows DPAPI / macOS Keychain / Linux libsecret). The app never holds the encryption key.
- The Minecraft session token is **never saved** — it's re-fetched from the refresh token each time you open the
  app, and lives only in memory.
- Sign out = full purge (the refresh token is removed from the OS vault). Only your **username + UUID** (both
  public info) are cached in plaintext, so the app can show "signed in as …" instantly.

To enable sign-in, the app needs a **Microsoft app registration** (an "Azure app"). Microsoft doesn't hand out
a shared one, so you register your own free one. It's a **public client** — it has **no secret**, so the client
id is safe to keep in the app's source. This is exactly what PrismLauncher / HMCL / other open launchers do.

---

## Part A — Register the Azure app (~5 minutes, one time, free)

1. Go to the **Microsoft Entra admin center**: <https://entra.microsoft.com>
   (or the classic Azure Portal → "App registrations" — either works). Sign in with any Microsoft account.
2. Left menu → **Applications** → **App registrations** → **+ New registration**.
3. Fill in:
   - **Name:** `Ant's Modding Tools` (anything — only you see it).
   - **Supported account types:** choose **"Personal Microsoft accounts only"**
     (if you also want work/school accounts, "Accounts in any organizational directory and personal Microsoft
     accounts" is fine too — but personal-only is the clean choice for a game launcher).
   - **Redirect URI:** platform dropdown → **Public client/native (mobile & desktop)**, value **`http://localhost`**.
   - Click **Register**.
4. On the app's **Overview** page, copy the **Application (client) ID** — a GUID like
   `12345678-abcd-1234-abcd-1234567890ab`. **This is what you paste into AMT.** (It is NOT a secret.)
5. Left menu → **Authentication**:
   - Under **Advanced settings**, set **"Allow public client flows"** to **Yes**. Save.
   - Confirm the redirect URI **`http://localhost`** is listed under "Mobile and desktop applications"
     (add it if it isn't). You can also add `http://127.0.0.1` as a second one.
6. That's it for Azure — you do **not** need a client secret, and you do **not** need to add API permissions
   manually (the app requests the `XboxLive.signin` scope at runtime).

## Part B — Get the client id approved for the Minecraft API (do this early — it can take a while)

A brand-new client id is **blocked by Mojang** from the Minecraft profile API until they approve it (you'll get
an HTTP 403 until then — sign-in gets all the way through Microsoft/Xbox and then fails at the last step).

1. Go to **<https://aka.ms/mce-reviewappid>** ("Existing AppID for Review/Report").
2. Submit your **Application (client) ID** from Part A step 4 for review.
3. Wait for approval (this is a Mojang/Microsoft process; do it first so it's ready when you test).

Until this approval lands, sign-in can't complete end-to-end **for anyone** — it's a Mojang gate, not an AMT bug.

## Part C — Put the client id into AMT

1. Open **Ant's Modding Tools → Settings → Launch → Minecraft account**.
2. Paste your **client id** into the client-id field (or, if you're building AMT yourself and it's *your* app for
   everyone, set `MsAccount.DefaultClientId` in `Amt.Core/MsAccount.cs` — it's public and safe in source).
3. Click **Sign in**. Your browser opens Microsoft's sign-in page → sign in → it says "you can close this tab".
4. AMT shows **"signed in as <your name>"**. The green **▶ Play** button now boots the game as your account.

## Part D — Set the launch command (what ▶ Play runs)

Settings → **Launch → Launch command**. This is the command AMT runs in the instance folder to boot the game.
Placeholders get filled from your signed-in account:

| placeholder | becomes |
|---|---|
| `{accessToken}` | your Minecraft session token |
| `{username}` | your Minecraft username |
| `{uuid}` | your account UUID |
| `{instance}` | the instance folder path |

Example (this pack's java arg-file):

```
"C:\Program Files\Eclipse Adoptium\jdk-25.0.3.9-hotspot\bin\java.exe" @.uvrun\launch-world.args
```

---

## Security & privacy summary (what's stored, where)

| Item | Stored? | Where | Encrypted? |
|---|---|---|---|
| Your password | **Never** | — | — (you type it on microsoft.com, not here) |
| Microsoft refresh token | Yes (the one secret) | OS secure vault (DPAPI / Keychain / libsecret) | **Yes, by the OS** |
| Minecraft session token | **No** | in memory only, re-fetched each launch | — |
| Xbox / XSTS tokens | **No** | in memory only | — |
| Username + UUID | Yes | `%APPDATA%\AntsModdingTools\profile.json` | No (public info) |
| Azure client id | Yes | your settings (or app source) | No (not a secret) |

To revoke AMT's access to your Microsoft account entirely (beyond signing out locally), visit
**<https://account.live.com/consent/Manage>** → "Apps and services you've given access to".

*Sources: Microsoft identity platform docs (MSAL.NET, token cache, PKCE), the Minecraft auth flow, and how
PrismLauncher/HMCL handle public client ids. Device-code flow is deliberately NOT used — Microsoft is blocking
it as a phishing vector; AMT uses authorization-code + PKCE via your system browser.*
