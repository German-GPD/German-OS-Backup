# German OS Backup

> **Windows x64 · Rust Edition и C# Edition** · [Релизы и установщики](https://github.com/German-GPD/German-OS-Backup/releases)

Локальное резервное копирование без рекламы, аккаунтов и телеметрии. Выбери редакцию — у каждой собственный установщик, EXE и подпись в интерфейсе.

## Скачать

Выбери одну из двух редакций:

[![Скачать Rust Edition](https://img.shields.io/badge/↓_Скачать-Rust_Edition-9bf0ad?style=for-the-badge&logo=rust&logoColor=102017)](https://github.com/German-GPD/German-OS-Backup/releases/latest/download/GermanOSBackup-Rust-Setup-x64.exe)  
[![Скачать C# Edition](https://img.shields.io/badge/↓_Скачать-C%23_Edition-252d28?style=for-the-badge&logo=dotnet&logoColor=ffffff)](https://github.com/German-GPD/German-OS-Backup/releases/latest/download/GermanOSBackup-CS-Setup-x64.exe)

- **Rust Edition** — нативный интерфейс без WebView2. Установщик: `GermanOSBackup-Rust-Setup-x64.exe`; приложение: `GermanOSBackup-Rust.exe`.
- **C# Edition** — Windows Forms + WebView2, системный трей и проверка обновлений. Установщик: `GermanOSBackup-CS-Setup-x64.exe`; приложение: `GermanOSBackup-CS.exe`.

Оба установщика публикуются одним релизом только после успешной сборки обеих редакций.

Сайт: https://german-gpd.github.io/German-OS-Backup/

## Ветки

- `main` — сайт, README, общая документация и единая публикация релизов.
- `rust` — исходный код Rust Edition.
- `cs` — исходный код C# Edition.

## Возможности

- Локальное резервное копирование выбранных папок на отдельный диск.
- Ручное сканирование источников и копирование новых или изменившихся файлов.
- Копии не удаляются автоматически при удалении оригиналов.
- Выбор источников и папки назначения.
- Счётчики скопированных и пропущенных файлов, ошибки и скорость.
- Мониторинг загрузки CPU и памяти.
- Проверка целостности по SHA-256, восстановление отсутствующих файлов и создание ZIP-архива в Rust Edition.
- C# Edition включает системный трей, журнал, режимы нагрузки, проверку обновлений и встроенную панель WebView2. Копирование запускается пользователем вручную.
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
