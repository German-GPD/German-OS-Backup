# German OS Backup · C# Edition

> **Windows 10/11 · x64 · .NET 10 + Windows Forms + WebView2**  
> [Сайт проекта](https://german-gpd.github.io/German-OS-Backup/) · [Скачать C# Edition](https://github.com/German-GPD/German-OS-Backup/releases/latest/download/GermanOSBackup-CS-Setup-x64.exe) · [Все релизы](https://github.com/German-GPD/German-OS-Backup/releases)

Классическая Windows-редакция для локального резервного копирования. Без рекламы, аккаунтов и телеметрии.

## Возможности

- Ручное копирование выбранных папок; можно остановить активную операцию.
- Системный трей и управление фоновым окном.
- Мониторинг CPU/RAM, скорость, журнал и режимы нагрузки.
- Проверка целостности по SHA-256.
- Восстановление отсутствующих файлов без перезаписи существующих.
- Создание ZIP-архива.
- Ручная проверка официальных обновлений через GitHub Releases; SHA-256 установщика проверяется до запуска.
- Локальные настройки и журнал.

## Скачать и запустить

1. Скачай **GermanOSBackup-CS-Setup-x64.exe** из раздела релизов.
2. Установи приложение и заверши мастер первого запуска.
3. Выбери источники и папку назначения на другом диске.
4. Проверь пути и вручную запусти копирование.

## Сборка из исходников

Нужны .NET 10 SDK и Inno Setup 6.

```powershell
dotnet publish .\src\GermanOSBackup\GermanOSBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
iscc .\installer\GermanOSBackup-CS.iss
```

Исполняемый файл: `GermanOSBackup-CS.exe`.

## Важные ограничения

Это файловая копия, а не образ Windows. Заблокированные или недоступные файлы могут быть пропущены; смотри журнал. Восстановление не перезаписывает существующие файлы. Для важных данных держи ещё одну копию на отдельном физическом носителе.

## Ветки и лицензия

Эта ветка — `cs`. Общий сайт, документация и единый выпуск двух установщиков находятся в `main`; исходники Rust — в ветке `rust`.

MIT. [Сообщить об ошибке](https://github.com/German-GPD/German-OS-Backup/issues).
