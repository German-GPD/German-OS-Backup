# German OS Backup

A quiet Windows background file-backup utility.

> **Early development (0.1.0).** It has not yet been validated across enough Windows systems to promise bug-free backups. Test recovery and keep a second copy of important data.

## Planned behavior
- First-run destination picker.
- Copy `C:\Users`, `C:\ProgramData`, and `C:\XboxGames` when present.
- Copy new and changed files; never propagate deletions.
- File-system notifications plus periodic rescans, with low CPU priority.
- No accounts, analytics, telemetry, ads, or network calls.

## Limitations
This is not a bootable system image. Locked/protected files may be skipped until a later scan. XboxGames can be very large. Reparse points should be skipped to avoid loops. Live game saves may change during copying, so file backup cannot guarantee application-consistent snapshots. The first copy may use noticeable disk bandwidth.

## Build
The intended release is a self-contained Windows x64 executable built with C# and .NET 10. Releases will be published from GitHub Actions after the build is validated.

## License
MIT. See [LICENSE](LICENSE).
