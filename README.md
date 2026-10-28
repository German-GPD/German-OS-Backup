# German OS Backup

> **Windows x64 · Rust Edition и C# Edition** · [Релизы и установщики](https://github.com/German-GPD/German-OS-Backup/releases)

Локальное резервное копирование без рекламы, аккаунтов и телеметрии. Выбери редакцию — у каждой собственный установщик, EXE и подпись в интерфейсе.

## Скачать

- **[↓ German OS Backup Rust Edition](https://github.com/German-GPD/German-OS-Backup/releases/latest/download/GermanOSBackup-Rust-Setup-x64.exe)** — нативный интерфейс на Rust, без WebView2. EXE: `GermanOSBackup-Rust.exe`.
- **[↓ German OS Backup C# Edition](https://github.com/German-GPD/German-OS-Backup/releases/latest/download/GermanOSBackup-CS-Setup-x64.exe)** — Windows Forms + WebView2. EXE: `GermanOSBackup-CS.exe`.

Обе ссылки указывают на файлы одного релиза. Они станут доступны после публикации нового релиза с двумя установщиками.

Сайт: https://german-gpd.github.io/German-OS-Backup/

## Ветки

- `main` — основная версия на Rust.
- `rust` — отдельная ветка Rust Edition.
- `cs` — отдельная ветка C# Edition.

## Возможности

- Локальное резервное копирование выбранных папок на отдельный диск.
- Повторное сканирование и копирование изменившихся файлов.
- Копии не удаляются автоматически при удалении оригиналов.
- Выбор источников и папки назначения.
- Счётчики скопированных и пропущенных файлов, ошибки и скорость.
- Мониторинг загрузки CPU и памяти.
- Проверка целостности по SHA-256 и восстановление отсутствующих файлов в Rust Edition.
- C# Edition включает системный трей, журнал, проверку обновлений и встроенную панель WebView2.
- Без рекламы, аккаунтов, аналитики и фоновой телеметрии.

## Сборка

### Rust Edition

Требуется стабильный Rust toolchain и Windows SDK для цели `x86_64-pc-windows-msvc`.

```powershell
cargo build --release --target x86_64-pc-windows-msvc
```

### C# Edition

Требуются .NET 10 SDK и Inno Setup 6.

```powershell
dotnet publish .\src\GermanOSBackup\GermanOSBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
iscc .\installer\GermanOSBackup-CS.iss
```

## Безопасность и ограничения

Это файловая резервная копия, а не образ Windows и не средство полного восстановления системы. Доступ к защищённым и занятым файлам зависит от разрешений Windows. Для важных данных держи дополнительную копию на отдельном физическом носителе.

## Лицензия

MIT. [Сообщить об ошибке](https://github.com/German-GPD/German-OS-Backup/issues).
