# German OS Backup

**Версия:** 1.4.3 · **Windows 10/11 x64**

[Скачать установщик](https://github.com/German-GPD/German-OS-Backup/releases/latest) · [Сайт](https://german-gpd.github.io/German-OS-Backup/) · [Исходный код](https://github.com/German-GPD/German-OS-Backup) · [Сообщить об ошибке](https://github.com/German-GPD/German-OS-Backup/issues)

German OS Backup — локальная программа для резервного копирования выбранных папок Windows. Без аккаунтов, рекламы и телеметрии. Настройки и журнал хранятся локально.

## Возможности
- Выбор папок-источников и отдельного каталога назначения.
- Полная сверка при запуске и вручную; обновление по событиям файловой системы для выбранных папок.
- Временный файл и атомарная замена: неудачное копирование не должно уничтожать последнюю исправную копию.
- Сохранение копий после удаления оригинала.
- Режимы производительности, статистика, журнал ошибок и системный трей.
- Проверка целостности по SHA-256.
- Восстановление выбранных файлов без перезаписи существующих.
- Создание ZIP-архива резервной папки и необязательный запуск вместе с Windows.
- Один установщик для Windows x64 с проверкой Microsoft WebView2 Runtime.

## Установка
1. Открой [последний релиз](https://github.com/German-GPD/German-OS-Backup/releases/latest).
2. Скачай GermanOSBackup-Setup-x64.exe и проверь SHA-256 по файлу SHA256SUMS.txt.
3. Установи программу и при первом запуске выбери источники и папку назначения.
4. Для важных данных используй второй независимый физический носитель и периодически проверяй восстановление.

Программа копирует выбранные файлы и папки, а не создаёт образ Windows. Заблокированные файлы, недоступные каталоги, отключённые диски и ошибки файловой системы могут помешать копированию. Проверяй журнал и сначала тестируй восстановление на временных файлах.

## Конфиденциальность и безопасность
- Содержимое файлов не отправляется в облако программой резервного копирования.
- Проверка обновлений обращается к GitHub только по запросу пользователя.
- Журнал может содержать локальные пути и сообщения об ошибках. Удаляй личные данные перед публикацией журнала.
- Установщик не подписан Authenticode; SmartScreen может предупреждать о неизвестном издателе. Не отключай защиту Windows ради установки.
- Сообщения об уязвимостях см. в [SECURITY.md](SECURITY.md).

## Сборка и тестирование
Требуются Windows, .NET 10 SDK, стабильный Rust toolchain с целью x86_64-pc-windows-msvc и Inno Setup 6.

    cargo fmt --manifest-path .\native-engine\Cargo.toml -- --check
    cargo test --manifest-path .\native-engine\Cargo.toml --target x86_64-pc-windows-msvc
    cargo clippy --manifest-path .\native-engine\Cargo.toml --all-targets --target x86_64-pc-windows-msvc -- -D warnings
    dotnet restore .\src\GermanOSBackup\GermanOSBackup.csproj
    dotnet build .\src\GermanOSBackup\GermanOSBackup.csproj -c Release --no-restore

GitHub Actions проверяет форматирование и тесты Rust, собирает приложение, упаковывает установщик и создаёт SHA-256. Успешная сборка не заменяет реальный тест восстановления на Windows.

## Разработка
Рабочая ветка — main. Код приложения находится в src/GermanOSBackup/, встроенный интерфейс — в dashboard.html, нативный модуль — в native-engine/, сценарий установщика — в installer/. Проверки согласованности выполняет scripts/repository-audit.ps1.

Перед отправкой изменений прочитай [CONTRIBUTING.md](CONTRIBUTING.md).

## Лицензия
MIT. См. [LICENSE](LICENSE).
