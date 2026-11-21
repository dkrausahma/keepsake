# What to back up and exclusions

The tabs **What to back up** and **Exclusions** of the job editor decide which files a job saves. You tick folders and files in a tree, untick what you do not need inside them, and add patterns for files that should never be backed up, such as temporary files.

## Select folders and files

The tree on the **What to back up** tab lists the drives of this computer. Expand a drive or folder to see its content.

- **Tick** a folder or file to back it up. A ticked folder includes everything below it, including folders created later.
- **Untick** an item inside a ticked folder to exclude it. This is an "exclusion by selection".
- Folder hints help you find your selection in a collapsed tree: **some items excluded** on a ticked folder, **contains selected items** on an unticked folder.

The right side lists the result:

- **Backed up**: the top-level sources of the job. **Remove** takes a source out.
- **Excluded by selection**: items unticked inside a source. **Include again** removes the exclusion.

You cannot tick an item inside a folder you unticked. Keepsake then shows *This item is inside an excluded folder. Tick that folder again to include it.*

## Add network paths and paths not in the tree

Enter the path in the field **Add a path that is not in the tree (e.g. \\server\share\folder)** and click **Add**. The path must be absolute: `D:\Data` or `\\server\share\folder`.

!!! warning "Use UNC paths, not mapped drive letters"
    The tree shows the drives of your own session, including mapped network drives. The Keepsake service runs as LocalSystem and does not see your mapped drives. Add network sources as `\\server\share\folder`.

### Credentials for network shares

The service reaches a share with the computer account (`DOMAIN\SERVER$`) unless you store credentials. To use another account, open the [Options](job-options.md) tab and add the share under **Network shares used as sources** with **Share (\\server\share)**, **User (DOMAIN\user)** and **Password**. The credentials are used only for the connection to that share.

## Exclusion patterns

On the **Exclusions** tab, enter patterns in **Exclusion patterns – one per line**. Matching files and folders are skipped; a matching folder is skipped with everything inside it. Upper and lower case do not matter.

| Pattern | Matches |
|---|---|
| `*.tmp` | Every file or folder whose name ends in `.tmp`, anywhere. |
| `~$*` | Office lock files such as `~$Report.docx`. |
| `Thumbs.db` | Every file or folder with exactly this name. |
| `cache\` | Every **folder** named `cache`, anywhere. Files named `cache` are kept. |
| `D:\Data\Temp` | Exactly this path. |
| `D:\Data\**\obj\` | Every folder named `obj` at any depth below `D:\Data`. |
| `bin\Debug` | `Debug` directly inside a `bin` folder, at any depth. |

The rules:

- **Without `\`** the pattern matches the file or folder **name**. Here `*` matches any characters and `?` one character.
- **A trailing `\`** restricts the pattern to folders.
- **With `\`** the pattern matches the **full path**. `*` and `?` stay within one folder level; `**` spans any number of levels. A path pattern that does not start with a drive letter, `\\` or `**` matches at any depth.
- `/` is treated like `\`.
- Lines starting with `#` are ignored.

Regular expressions, size limits and age limits are not supported.

!!! note
    Comment lines (`#`) are not kept when you save the job. A plain full path that lies inside a source, such as `D:\Data\Temp`, appears under **Excluded by selection** the next time you open the job.

There is no preview of which files a pattern matches. To see what was excluded, run the backup and look at the run in [History and logs](history.md).

### Standard system items

**Also exclude the standard system items** is on by default. It excludes:

- `$RECYCLE.BIN\`
- `System Volume Information\`
- `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`
- `~$*`

Leave it on unless you have a reason to back up these items.

## Special files and folders

- **Junctions and symbolic links to folders** are not followed. Keepsake records a warning *Junction or symbolic link – the linked folder is not followed.* and the link itself is not backed up. Back up the target folder directly if you need it. The tree hides such folders.
- **Long paths**: paths longer than 260 characters are read and backed up.
- **Open and locked files** are read from a VSS shadow copy when **Use VSS snapshots so open and locked files are backed up (recommended)** is on (see [Options](job-options.md)). Without VSS, a locked file is skipped with the message *File is locked by another program. Enable VSS for the job to back up open files.* and the run ends with warnings.
- **Unreadable files** (for example *Access denied.*) are skipped and listed as not backed up in the run log; the run ends with warnings. The rest of the backup continues.
