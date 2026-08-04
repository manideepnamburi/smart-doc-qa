# API Key Health Check

Tests every configured API key with a minimal real call, distinguishes an
actual auth failure (revoked/rotated key) from a transient network error, and
emails an alert only on the former. Logs every run either way.

## Why this exists

We hit a real 401 mid-eval-run — a key that had quietly stopped working, with
no way to know until something else failed downstream. This catches that
*before* it costs you a debugging session, by checking daily instead of
finding out the hard way.

## Setup

### 1. Configure which keys to check (`keys-to-check.json`)

Already committed with sensible defaults. Set `"enabled": true` on whichever
keys you want checked; each entry only names an **environment variable**, never
a real key.

### 2. Configure email delivery (`email-config.json`)

Provider is configurable — currently `"smtp"` is implemented (works with Gmail,
Outlook, or any SMTP server). Fill in your real `fromAddress`/`toAddress` (not
secrets, safe to commit) and leave `credentialEnvVar` pointing at whatever env
var will hold your real password.

**For Gmail specifically:** you need an **App Password**, not your normal
Gmail password (Google blocks plain-password SMTP login):
1. Enable 2-Step Verification on your Google account if not already on.
2. Go to `myaccount.google.com/apppasswords`.
3. Generate a new app password for "Mail" / "Other (custom name)".
4. Use that 16-character password as `KEYCHECK_SMTP_PASSWORD` below — not
   your real Gmail password.

For Outlook/Office365, set `smtpHost` to `smtp.office365.com`, port `587`,
`useSsl: true` — your normal Microsoft account password usually works there
without an app password, but if your org enforces MFA, generate an app
password the same way.

### 3. Real secrets go in a gitignored local script

Copy `run-key-check.local.ps1`, fill in your real key(s) and SMTP password.
This file is already covered by the same `.local.ps1` gitignore pattern used
elsewhere in this repo — verify:
```powershell
git check-ignore -v tools\key-health-check\run-key-check.local.ps1
```

### 4. Run it manually first

```powershell
cd tools\key-health-check
.\run-key-check.local.ps1
```

Check `key-health-log.txt` — you should see `OK` lines for each enabled key.
Temporarily set a wrong value for one key's env var and re-run to confirm the
email alert actually fires before relying on it.

## Running on a schedule (Windows Task Scheduler)

1. Open **Task Scheduler** → **Create Basic Task**.
2. Name: `SmartDocQA Key Health Check`. Trigger: **Daily**, pick a time.
3. Action: **Start a program**.
   - Program: `powershell.exe`
   - Arguments: `-ExecutionPolicy Bypass -File "C:\Users\nambu\OneDrive\Learning\SmartDocQA\tools\key-health-check\run-key-check.local.ps1"`
4. Finish, then right-click the task → **Run** once to confirm it works
   outside of an interactive session (scheduled tasks run in a different
   context — env vars set only in your terminal session won't be visible to
   it, which is exactly why the real secrets live inside
   `run-key-check.local.ps1` itself rather than relying on ambient env vars).
5. Check `key-health-log.txt` after the scheduled run to confirm it executed.

## Extending to a new provider

Add a `Test-<Provider>Key` function following the same pattern (minimal real
call, catch the status code, return `Success`/`ErrorType`/`Message`), then add
a case for it in the `switch` block in `Key-Health-Check.ps1`, plus a new
entry in `keys-to-check.json`.

## Possible future enhancement (not implemented)

Right now, a still-broken key emails an alert on every scheduled run (once a
day) until fixed. If that becomes noisy, a simple addition would be tracking
last-known-status in a small state file and only alerting on a *change*
(working → broken), not on every subsequent daily check while it stays broken.
