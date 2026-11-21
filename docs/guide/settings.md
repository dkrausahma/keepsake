# Settings

The **Settings** page holds the settings of the Keepsake service on this computer: how many jobs may run at the same
time, how long run logs are kept, the debug log, the SMTP server for e-mail notifications and the application
password. Everything that belongs to a single backup job – sources, destination, schedule, retention, which events send
a mail – is set in the job editor instead (see [The Backup jobs page](jobs.md)).

At the top, the page shows the Keepsake version, the computer name, since when the service runs and its
**Data folder** (by default `%ProgramData%\Keepsake`).

Each section has its own **Save** button. A message next to the button confirms `Saved.` or explains what is wrong.

## Service

| Field | Default | Allowed | Meaning |
|---|---|---|---|
| **Jobs that may run at the same time** | 1 | 1–8 | Maximum number of runs (backup, restore, check, cleanup) the service executes in parallel. Further runs wait as *Queued*. |
| **Keep run logs for (days)** | 90 | 1–3650 | Run logs older than this are deleted. |
| **… and at most (runs per job)** | 200 | 1–10000 | Only this many newest runs per job are kept. |
| **Write a debug log (SFTP connection details for support, no passwords)** | off | | Writes SFTP connection details to a log file for support. |

Both retention limits apply: a run is deleted when it is too old **or** beyond the number of runs. The service cleans up
once a day. Runs that are still queued or running are never deleted. See [History and logs](history.md).

When the debug log is on, the page shows its path: `%ProgramData%\Keepsake\logs\debug.log` (only readable by
administrators; at most 5 MB, then the older part moves to `debug.log.1`). It records the SSH algorithms negotiated and
connection failures, never passwords or keys. Switch it off again after the support case.

!!! tip
    Raising the number of parallel jobs only helps when jobs write to different destinations. Several jobs reading
    from the same disk or writing over the same line usually get slower, not faster.

## E-mail notifications

Leave **SMTP server** empty to turn e-mail off. Which events send a mail is set per job on the
**Retention & checks** tab of the job editor; see [Retention and checks](job-retention.md) and
[Monitoring and notifications](monitoring.md).

| Field | Default | Meaning |
|---|---|---|
| **SMTP server** | – | Host name or IP address of the mail server. |
| **Port / security** | `587`, `starttls` | Port 1–65535 and the encryption. |
| **User (optional)** | – | Login name. Leave empty for a relay that accepts mail without login. |
| **Password** | – | Login password. Stored encrypted (DPAPI) on this computer. When a password is stored, the page shows `A password is stored; leave empty to keep it.` |
| **From** | `keepsake@<computer name>` | Sender address. |
| **To (; separated)** | – | One or more recipients, separated by `;` (or `,`). At least one is needed. |

Security options:

| Option | Typical port | Behaviour |
|---|---|---|
| `starttls` | 587 | Connects in plain text and switches to TLS (STARTTLS). |
| `ssl` | 465 | TLS from the start (implicit SSL). |
| `none` | 25 | No encryption. Only for internal relays. |

Keepsake uses TLS 1.2 (and TLS 1.3 on Windows Server 2022 and newer). Windows Server 2008 R2 needs TLS 1.2 enabled first;
see [Windows Server 2008 R2](../WINDOWS-2008R2.md).

!!! warning
    With `none` and a user name, the password and the report travel in clear text. The service writes a warning to its
    log each time. Use `starttls` or `ssl` whenever the server supports it.

Click **Save** before **Send test e-mail**: the test always uses the saved settings. The test sends
`[Keepsake] Test message from <computer>` to all recipients. On success the page shows `Test e-mail sent to …`; otherwise
it shows the reason, for example
`E-mail could not be sent via mail.example.com:587: …`.

Invalid input is rejected on save, for example `'x@' is not a valid e-mail address.` or
`The SMTP port must be between 1 and 65535.`

## Application password

The application password protects access to Keepsake on this computer – this program and the `keepsake` command line.
It is not a repository password and does not encrypt anything; changing it does not affect any backup.

1. Enter the **Current password**.
2. Enter the **New password (at least 8 characters)** and **Repeat new password**.
3. Click **Change password**. The page confirms `Application password changed.`

Messages you may see: `The new passwords do not match.`, `The current application password is wrong.`,
`The application password must have at least 8 characters.` Wrong passwords count toward the login lockout (see
[Troubleshooting](troubleshooting.md)).

For the difference between the application password, repository passwords and recovery keys, see
[Passwords and keys](../PASSWORDS-AND-KEYS.md).
