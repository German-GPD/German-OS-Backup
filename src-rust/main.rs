#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use eframe::egui::{self, Color32, RichText, Stroke, Vec2};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io,
    path::{Path, PathBuf},
    sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}},
    thread,
    time::{Duration, SystemTime},
};
use sysinfo::System;
use walkdir::WalkDir;

const VERSION: &str = env!("CARGO_PKG_VERSION");
const RELEASES: &str = "https://github.com/German-GPD/German-OS-Backup/releases/latest";

#[derive(Clone, Serialize, Deserialize)]
struct Config {
    #[serde(rename = "Destination", alias = "destination")]
    destination: String,
    #[serde(rename = "SelectedSources", alias = "sources", default)]
    sources: Vec<String>,
    #[serde(rename = "PerformanceMode", alias = "performance_mode", default = "default_mode")]
    performance_mode: String,
    #[serde(rename = "AutoUpdateSources", alias = "auto_update_sources", default)]
    auto_update_sources: Vec<String>,
}
fn default_mode() -> String { "balanced".into() }
impl Default for Config {
    fn default() -> Self {
        let home = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".into());
        let home = PathBuf::from(home);
        let mut sources = Vec::new();
        for name in ["Desktop", "Documents", "Pictures", "Videos"] {
            let p = home.join(name);
            if p.is_dir() { sources.push(p.to_string_lossy().to_string()); }
        }
        Self {
            destination: String::new(),
            sources,
            performance_mode: "balanced".into(),
            auto_update_sources: Vec::new(),
        }
    }
}

#[derive(Default, Clone)]
struct Stats {
    running: bool,
    cancellable: bool,
    files_seen: u64,
    files_copied: u64,
    files_skipped: u64,
    errors: u64,
    bytes_copied: u64,
    speed: u64,
    status: String,
    started: Option<SystemTime>,
}

struct BackupApp {
    config: Config,
    config_path: PathBuf,
    stats: Arc<Mutex<Stats>>,
    cancel: Arc<AtomicBool>,
    system: System,
    cpu: f32,
    memory: f32,
    memory_used: u64,
    memory_total: u64,
    last_stats: Stats,
    last_tick: std::time::Instant,
    log: Vec<String>,
    watcher_cancel: Arc<AtomicBool>,
}
impl BackupApp {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        let mut style = (*cc.egui_ctx.style()).clone();
        style.visuals = egui::Visuals::dark();
        style.visuals.panel_fill = Color32::from_rgb(13, 16, 15);
        style.visuals.window_fill = Color32::from_rgb(17, 21, 18);
        style.visuals.widgets.noninteractive.bg_fill = Color32::from_rgb(19, 24, 21);
        style.visuals.widgets.inactive.bg_fill = Color32::from_rgb(25, 31, 27);
        style.visuals.widgets.hovered.bg_fill = Color32::from_rgb(34, 45, 37);
        style.visuals.widgets.active.bg_fill = Color32::from_rgb(45, 70, 51);
        style.visuals.selection.bg_fill = Color32::from_rgb(105, 190, 124);
        style.visuals.widgets.noninteractive.corner_radius = egui::CornerRadius::same(2);
        style.visuals.widgets.inactive.corner_radius = egui::CornerRadius::same(2);
        style.visuals.widgets.hovered.corner_radius = egui::CornerRadius::same(2);
        style.visuals.widgets.active.corner_radius = egui::CornerRadius::same(2);
        style.spacing.item_spacing = Vec2::new(10.0, 9.0);
        cc.egui_ctx.set_style(style);

        // Keep the same per-user config location as the previous C# version.
        let local_app_data = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into());
        let config_path = PathBuf::from(local_app_data).join("GermanOSBackup-Rust").join("config.json");
        let config = fs::read_to_string(&config_path)
            .ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default();
        let stats = Arc::new(Mutex::new(Stats { status: "Готово к работе".into(), ..Default::default() }));
        let mut app = Self { config, config_path, stats, cancel: Arc::new(AtomicBool::new(false)),
            system: System::new(), cpu: 0.0, memory: 0.0, memory_used: 0, memory_total: 0,
            last_stats: Stats::default(), last_tick: std::time::Instant::now(), log: vec![
                format!("German OS Backup Rust Edition {VERSION} запущен"),
                "Данные хранятся локально; телеметрии нет".into()
            ], watcher_cancel: Arc::new(AtomicBool::new(false)) };
        app.restart_auto_watchers();
        app
    }

    fn new_webview() -> Self {
        let local_app_data = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into());
        let config_path = PathBuf::from(local_app_data)
            .join("GermanOSBackup-Rust")
            .join("config.json");
        let config = fs::read_to_string(&config_path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default();
        let stats = Arc::new(Mutex::new(Stats {
            status: "Готово к работе".into(),
            ..Default::default()
        }));
        let mut app = Self {
            config,
            config_path,
            stats,
            cancel: Arc::new(AtomicBool::new(false)),
            system: System::new(),
            cpu: 0.0,
            memory: 0.0,
            memory_used: 0,
            memory_total: 0,
            last_stats: Stats::default(),
            last_tick: std::time::Instant::now(),
            log: vec![
                format!("German OS Backup Rust Edition {VERSION} запущен"),
                "Данные хранятся локально; телеметрии нет".into(),
            ],
            watcher_cancel: Arc::new(AtomicBool::new(false)),
        };
        app.restart_auto_watchers();
        app
    }

    fn save_config(&mut self) {
        if let Some(parent) = self.config_path.parent() { let _ = fs::create_dir_all(parent); }
        if let Ok(json) = serde_json::to_string_pretty(&self.config) {
            let temp = self.config_path.with_extension("json.tmp");
            if fs::write(&temp, json).is_ok() {
                // Windows rename cannot replace an existing destination; replace safely.
                let backup = self.config_path.with_extension("json.bak");
                let _ = fs::remove_file(&backup);
                if self.config_path.exists() { let _ = fs::rename(&self.config_path, &backup); }
                if fs::rename(&temp, &self.config_path).is_ok() { let _ = fs::remove_file(backup); }
                else if backup.exists() { let _ = fs::rename(backup, &self.config_path); }
            }
        }
    }

    fn add_log(&mut self, line: impl Into<String>) {
        self.log.push(line.into());
        if self.log.len() > 8 { self.log.remove(0); }
    }

    fn restart_auto_watchers(&mut self) {
        self.watcher_cancel.store(true, Ordering::SeqCst);
        let cancel = Arc::new(AtomicBool::new(false));
        self.watcher_cancel = cancel.clone();
        let destination = match fs::canonicalize(&self.config.destination) {
            Ok(path) => path,
            Err(_) => return,
        };
        let sources = self.config.sources.iter()
            .filter(|s| self.config.auto_update_sources.iter().any(|a| a.eq_ignore_ascii_case(s)))
            .filter_map(|s| fs::canonicalize(s).ok().filter(|p| p.is_dir()).map(|p| p.to_string_lossy().to_string()))
            .filter(|s| {
                let source = Path::new(s);
                !source.starts_with(&destination) && !destination.starts_with(source)
            })
            .collect::<Vec<_>>();
        let stats = self.stats.clone();
        if sources.is_empty() { return; }
        thread::spawn(move || {
            use notify::{RecommendedWatcher, RecursiveMode, Watcher};
            use std::sync::mpsc::{channel, RecvTimeoutError};
            let (tx, rx) = channel();
            let mut watcher: RecommendedWatcher = match notify::recommended_watcher(move |event| { let _ = tx.send(event); }) {
                Ok(w) => w,
                Err(_) => { if let Ok(mut s) = stats.lock() { s.status = "Не удалось запустить динамическое отслеживание".into(); } return; }
            };
            for source in &sources {
                if Path::new(source).is_dir() && watcher.watch(Path::new(source), RecursiveMode::Recursive).is_err() {
                    if let Ok(mut s) = stats.lock() { s.errors += 1; s.status = format!("Не удалось отслеживать: {source}"); }
                }
            }
            let mut pending: Vec<PathBuf> = Vec::new();
            while !cancel.load(Ordering::SeqCst) {
                match rx.recv_timeout(Duration::from_millis(300)) {
                    Ok(Ok(event)) => {
                        use notify::EventKind;
                        if matches!(event.kind, EventKind::Remove(_)) { continue; } // Keep the last backup when the original is deleted.
                        for path in event.paths {
                            if !pending.iter().any(|p| p == &path) { pending.push(path); }
                        }
                    },
                    Ok(Err(_)) => continue,
                    Err(RecvTimeoutError::Timeout) => {},
                    Err(RecvTimeoutError::Disconnected) => break,
                }
                if pending.is_empty() { continue; }
                let batch = std::mem::take(&mut pending);
                for path in batch {
                    if cancel.load(Ordering::SeqCst) { break; }
                    let Some((source, relative)) = sources.iter().find_map(|source| {
                        path.strip_prefix(source).ok().map(|relative| (source, relative.to_path_buf()))
                    }) else { continue; };
                    let source_path = Path::new(source);
                    let target_root = Path::new(&destination).join(source_path.file_name().unwrap_or_default());
                    let target = target_root.join(relative);
                    if path.is_dir() {
                        let _ = fs::create_dir_all(&target);
                        continue;
                    }
                    if !path.is_file() { continue; }
                    if stats.lock().map(|s| s.running).unwrap_or(true) {
                        pending.push(path);
                        thread::sleep(Duration::from_millis(250));
                        continue;
                    }
                    let before = match fs::metadata(&path) { Ok(m) => m, Err(_) => continue };
                    thread::sleep(Duration::from_millis(350));
                    let after = match fs::metadata(&path) { Ok(m) => m, Err(_) => continue };
                    if before.len() != after.len() || before.modified().ok() != after.modified().ok() {
                        pending.push(path);
                        continue;
                    }
                    if let Some(parent) = target.parent() { if fs::create_dir_all(parent).is_err() { continue; } }
                    match copy_file_atomically(&path, &target, after.modified().ok()) {
                        Ok(written) => {
                            if let Ok(mut s) = stats.lock() {
                                s.files_copied += 1;
                                s.bytes_copied += written;
                                s.status = format!("Автообновлено: {}", path.display());
                            }
                        }
                        Err(error) => {
                            if let Ok(mut s) = stats.lock() {
                                s.errors += 1;
                                s.status = format!("Ошибка автообновления {}: {error}", path.display());
                            }
                        }
                    }
                }
            }
        });
    }

    fn start_backup(&mut self) {
        if self.stats.lock().map(|s| s.running).unwrap_or(true) {
            self.add_log("Другая операция ещё выполняется — дождись её завершения");
            return;
        }
        if self.config.destination.trim().is_empty() {
            self.add_log("Сначала выбери папку назначения");
            return;
        }
        if self.config.sources.is_empty() {
            self.add_log("Сначала добавь папки-источники");
            return;
        }
        let dest = match fs::canonicalize(&self.config.destination) {
            Ok(path) => path,
            Err(_) => { self.add_log("Не удалось открыть папку назначения — проверь путь и доступ"); return; }
        };
        let system_drive = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into());
        let system_root = PathBuf::from(format!("{}\\", system_drive.trim_end_matches(&['\\', '/'][..])));
        let dest_text = dest.to_string_lossy().trim_start_matches("\\\\?\\").to_lowercase();
        let system_text = system_root.to_string_lossy().to_lowercase();
        if dest_text.starts_with(&system_text) {
            self.add_log("Для резервной копии выбери отдельный диск, не системный");
            return;
        }
        let mut canonical_sources: Vec<PathBuf> = Vec::new();
        let mut root_names: Vec<String> = Vec::new();
        for source in &self.config.sources {
            let src = match fs::canonicalize(source) {
                Ok(path) if path.is_dir() => path,
                _ => { self.add_log(&format!("Папка-источник недоступна: {source}")); return; }
            };
            let src_text = src.to_string_lossy().trim_start_matches("\\\\?\\").to_lowercase();
            if src_text == dest_text || src_text.starts_with(&(dest_text.clone() + "\\")) || dest_text.starts_with(&(src_text.clone() + "\\")) {
                self.add_log("Папка назначения пересекается с источником — копирование отменено");
                return;
            }
            if canonical_sources.iter().any(|other| {
                let a = other.to_string_lossy().to_lowercase();
                a == src_text || a.starts_with(&(src_text.clone() + "\\")) || src_text.starts_with(&(a + "\\"))
            }) {
                self.add_log("Источники пересекаются или повторяются — убери вложенные папки из списка");
                return;
            }
            let root = src.file_name().unwrap_or_default().to_string_lossy().to_lowercase();
            if root_names.iter().any(|name| name == &root) {
                self.add_log("У двух источников одинаковое имя папки — переименуй один или выбери другой источник");
                return;
            }
            root_names.push(root);
            canonical_sources.push(src);
        }
        self.save_config();
        self.cancel.store(false, Ordering::SeqCst);
        let cancel = self.cancel.clone();
        let stats = self.stats.clone();
        let sources = self.config.sources.clone();
        let performance_mode = self.config.performance_mode.clone();
        let destination = dest;
        if let Ok(mut s) = stats.lock() {
            *s = Stats { running: true, cancellable: true, status: "Подготовка к копированию…".into(), started: Some(SystemTime::now()), ..Default::default() };
        }
        self.add_log("Запущено копирование");
        thread::spawn(move || {
            let mut total_bytes = 0u64;
            let mut last_speed = std::time::Instant::now();
            let mut window_bytes = 0u64;
            for source_text in sources {
                if cancel.load(Ordering::SeqCst) { break; }
                let source = PathBuf::from(&source_text);
                if !source.is_dir() {
                    if let Ok(mut s) = stats.lock() { s.errors += 1; s.status = format!("Источник не найден: {}", source.display()); }
                    continue;
                }
                let root_name = source.file_name().map(|n| n.to_os_string()).unwrap_or_else(|| "Backup".into());
                let target_root = destination.join(root_name);
                for entry_result in WalkDir::new(&source).follow_links(false) {
                    let entry = match entry_result {
                        Ok(entry) => entry,
                        Err(error) => {
                            if let Ok(mut state) = stats.lock() {
                                state.errors += 1;
                                state.status = format!("Ошибка чтения резервируемой папки: {error}");
                            }
                            continue;
                        }
                    };
                    if cancel.load(Ordering::SeqCst) { break; }
                    let path = entry.path();
                    let relative = match path.strip_prefix(&source) { Ok(p) => p, Err(_) => continue };
                    let target = target_root.join(relative);
                    if entry.file_type().is_symlink() { continue; }
                    if entry.file_type().is_dir() {
                        if let Err(_) = fs::create_dir_all(&target) { if let Ok(mut s) = stats.lock() { s.errors += 1; } }
                        continue;
                    }
                    if !entry.file_type().is_file() { continue; }
                    if let Ok(mut s) = stats.lock() { s.files_seen += 1; s.status = format!("Копирование: {}", path.display()); }
                    let source_meta = match fs::metadata(path) { Ok(m) => m, Err(_) => { if let Ok(mut s) = stats.lock() { s.errors += 1; } continue; } };
                    let same = fs::metadata(&target).ok().map(|m| m.len() == source_meta.len() && m.modified().ok() == source_meta.modified().ok()).unwrap_or(false);
                    if same { if let Ok(mut s) = stats.lock() { s.files_skipped += 1; } continue; }
                    if let Some(parent) = target.parent() { if fs::create_dir_all(parent).is_err() { if let Ok(mut s) = stats.lock() { s.errors += 1; } continue; } }
                    match copy_file_atomically(path, &target, source_meta.modified().ok()) {
                        Ok(n) => {
                            total_bytes += n; window_bytes += n;
                            if let Ok(mut s) = stats.lock() { s.files_copied += 1; s.bytes_copied = total_bytes; }
                            if performance_mode == "eco" { thread::sleep(Duration::from_millis(25)); }
                            else if performance_mode == "balanced" { thread::sleep(Duration::from_millis(2)); }
                        },
                        Err(error) => {
                            if let Ok(mut s) = stats.lock() {
                                s.errors += 1;
                                s.status = format!("Ошибка копирования {}: {error}", path.display());
                            }
                        }
                    }
                    if last_speed.elapsed() >= Duration::from_secs(1) {
                        if let Ok(mut s) = stats.lock() { s.speed = window_bytes / last_speed.elapsed().as_secs().max(1); }
                        window_bytes = 0; last_speed = std::time::Instant::now();
                    }
                }
            }
            if let Ok(mut s) = stats.lock() {
                s.running = false; s.cancellable = false; s.speed = 0;
                s.status = if cancel.load(Ordering::SeqCst) { "Остановлено пользователем".into() }
                    else { "Копирование завершено".into() };
            }
        });
    }

    fn verify_backup(&mut self) {
        if self.stats.lock().map(|s| s.running).unwrap_or(true) {
            self.add_log("Другая операция ещё выполняется — дождись её завершения");
            return;
        }
        if self.config.destination.trim().is_empty() {
            self.add_log("Сначала выбери папку резервной копии");
            return;
        }
        let destination = PathBuf::from(&self.config.destination);
        let sources = self.config.sources.clone();
        let stats = self.stats.clone();
        if let Ok(mut s) = stats.lock() { s.running = true; s.cancellable = false; s.status = "Глубокая проверка SHA-256…".into(); }
        self.add_log("Запущена глубокая проверка SHA-256");
        thread::spawn(move || {
            let mut checked = 0u64; let mut missing = 0u64; let mut different = 0u64; let mut walk_errors = 0u64;
            for source_text in sources {
                let source = PathBuf::from(&source_text);
                if !source.is_dir() { continue; }
                let root_name = source.file_name().map(|n| n.to_os_string()).unwrap_or_else(|| "Backup".into());
                let target_root = destination.join(root_name);
                for entry_result in WalkDir::new(&source).follow_links(false) {
                    let entry = match entry_result {
                        Ok(entry) => entry,
                        Err(_) => { walk_errors += 1; continue; }
                    };
                    if !entry.file_type().is_file() { continue; }
                    let path = entry.path();
                    let relative = match path.strip_prefix(&source) { Ok(p) => p, Err(_) => continue };
                    let target = target_root.join(relative);
                    if !target.is_file() { missing += 1; continue; }
                    let hash_file = |p: &Path| -> std::io::Result<Vec<u8>> {
                        use std::io::Read;
                        let mut file = fs::File::open(p)?;
                        let mut hasher = Sha256::new();
                        let mut buffer = [0u8; 1024 * 1024];
                        loop { let n = file.read(&mut buffer)?; if n == 0 { break; } hasher.update(&buffer[..n]); }
                        Ok(hasher.finalize().to_vec())
                    };
                    match (hash_file(path), hash_file(&target)) {
                        (Ok(a), Ok(b)) => { checked += 1; if a != b { different += 1; } },
                        _ => different += 1,
                    }
                }
            }
            if let Ok(mut s) = stats.lock() {
                s.running = false; s.cancellable = false;
                s.errors += walk_errors;
                s.status = format!("Проверка завершена · проверено {checked}, отсутствует {missing}, различается {different}, ошибок обхода {walk_errors}");
            }
        });
    }

    fn restore_missing(&mut self) {
        if self.stats.lock().map(|s| s.running).unwrap_or(true) {
            self.add_log("Другая операция ещё выполняется — дождись её завершения");
            return;
        }
        let backup = rfd::FileDialog::new().set_title("Выбери корень резервной копии").pick_folder();
        let Some(backup) = backup else { return; };
        let target = rfd::FileDialog::new().set_title("Куда восстановить отсутствующие файлы").pick_folder();
        let Some(target) = target else { return; };
        if backup == target || target.starts_with(&backup) || backup.starts_with(&target) {
            self.add_log("Папка источника и папка восстановления не должны пересекаться");
            return;
        }
        let stats = self.stats.clone();
        if let Ok(mut s) = stats.lock() { s.running = true; s.cancellable = false; s.status = "Восстановление отсутствующих файлов…".into(); }
        self.add_log("Запущено восстановление без перезаписи существующих файлов");
        thread::spawn(move || {
            let mut restored = 0u64; let mut skipped = 0u64; let mut errors = 0u64;
            for entry_result in WalkDir::new(&backup).follow_links(false) {
                let entry = match entry_result {
                    Ok(entry) => entry,
                    Err(_) => { errors += 1; continue; }
                };
                if !entry.file_type().is_file() { continue; }
                let rel = match entry.path().strip_prefix(&backup) { Ok(p) => p, Err(_) => continue };
                // If the user selects a drive root (e.g. C:\\), restore known Windows
                // profile folders to their actual user-folder locations instead of
                // creating C:\\Изображения, C:\\Документы, etc.
                let out = restore_destination(&target, rel, &backup);
                if out.exists() { skipped += 1; continue; }
                if let Some(parent) = out.parent() { if fs::create_dir_all(parent).is_err() { errors += 1; continue; } }
                match restore_file_atomically(entry.path(), &out) {
                    Ok(true) => restored += 1,
                    Ok(false) => skipped += 1,
                    Err(_) => errors += 1,
                }
            }
            if let Ok(mut s) = stats.lock() {
                s.running = false; s.cancellable = false;
                s.status = format!("Восстановлено {restored} · пропущено {skipped} · ошибок {errors}");
                s.files_copied += restored; s.errors += errors;
            }
        });
    }

    fn create_zip(&mut self) {
        if self.stats.lock().map(|s| s.running).unwrap_or(true) {
            self.add_log("Другая операция ещё выполняется — дождись её завершения");
            return;
        }
        if self.config.destination.trim().is_empty() || !Path::new(&self.config.destination).is_dir() {
            self.add_log("Сначала создай резервную копию: выбери папку назначения и нажми «Начать копирование»");
            return;
        }
        let source = PathBuf::from(&self.config.destination);
        let Some(output) = rfd::FileDialog::new().set_title("Сохранить ZIP-архив резервной копии").add_filter("ZIP-архив", &["zip"]).set_file_name("GermanOSBackup.zip").save_file() else { return; };
        if output.starts_with(&source) { self.add_log("ZIP-архив нельзя сохранять внутри папки резервной копии"); return; }
        let stats = self.stats.clone();
        if let Ok(mut s) = stats.lock() { s.running = true; s.cancellable = false; s.status = "Создание ZIP-архива…".into(); }
        self.add_log("Создание ZIP-архива запущено вручную");
        thread::spawn(move || {
            let result = (|| -> Result<u64, Box<dyn std::error::Error + Send + Sync>> {
                let file = fs::File::create(&output)?;
                let mut archive = zip::ZipWriter::new(file);
                let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
                let mut count = 0u64;
                for entry_result in WalkDir::new(&source).follow_links(false) {
                    let entry = entry_result?;
                    if !entry.file_type().is_file() { continue; }
                    let relative = entry.path().strip_prefix(&source)?;
                    let root_name = source.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "Backup".to_string());
                    let name = format!("{}/{}", root_name, relative.to_string_lossy().replace('\\', "/"));
                    archive.start_file(name, options)?;
                    let mut input = fs::File::open(entry.path())?;
                    io::copy(&mut input, &mut archive)?;
                    count += 1;
                }
                archive.finish()?;
                Ok(count)
            })();
            if let Ok(mut s) = stats.lock() {
                s.running = false; s.cancellable = false;
                s.status = match result { Ok(count) => format!("ZIP-архив создан · файлов: {count}"), Err(ref e) => format!("Ошибка создания ZIP: {e}") };
            }
        });
    }

    fn stop_backup(&mut self) {
        self.cancel.store(true, Ordering::SeqCst);
        self.add_log("Отправлена команда остановки");
    }
}

impl eframe::App for BackupApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.system.refresh_cpu_usage();
        self.system.refresh_memory();
        self.cpu = self.system.global_cpu_usage();
        self.memory_used = self.system.used_memory();
        self.memory_total = self.system.total_memory();
        self.memory = if self.memory_total > 0 { self.memory_used as f32 / self.memory_total as f32 * 100.0 } else { 0.0 };
        if let Ok(s) = self.stats.lock() { self.last_stats = s.clone(); }
        ctx.request_repaint_after(Duration::from_millis(900));

        let green = Color32::from_rgb(151, 240, 171);
        let muted = Color32::from_rgb(137, 149, 140);
        egui::TopBottomPanel::top("header").frame(egui::Frame::new().fill(Color32::from_rgb(13,16,15)).inner_margin(egui::Margin::symmetric(22,18))).show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(RichText::new("G_").size(25.0).strong().color(green));
                ui.vertical(|ui| {
                    ui.label(RichText::new("GERMAN OS BACKUP · RUST EDITION").size(17.0).strong().color(Color32::WHITE));
                    ui.label(RichText::new(format!("ЛОКАЛЬНОЕ РЕЗЕРВНОЕ КОПИРОВАНИЕ · v{VERSION}")).size(10.0).color(muted));
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button("Открыть релизы ↗").clicked() { ctx.open_url(egui::OpenUrl::new_tab(RELEASES)); }
                    ui.label(RichText::new("●").color(green));
                });
            });
        });

        egui::CentralPanel::default().frame(egui::Frame::new().fill(Color32::from_rgb(13,16,15)).inner_margin(egui::Margin::symmetric(22,10))).show(ctx, |ui| {
            egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.heading(RichText::new("Состояние системы").size(21.0).color(Color32::WHITE));
                let (status_label, status_color) = if self.last_stats.running {
                    ("● В РАБОТЕ", green)
                } else if self.last_stats.errors > 0 {
                    ("● ПРОВЕРЬ ЖУРНАЛ", Color32::from_rgb(244, 202, 120))
                } else {
                    ("● ГОТОВО", muted)
                };
                ui.label(RichText::new(status_label).color(status_color).size(11.0).strong());
            });
            ui.add_space(6.0);
            let setup_hint = if self.config.destination.is_empty() {
                "НАЧНИ ЗДЕСЬ · выбери папку назначения, чтобы указать диск для резервной копии"
            } else if self.config.sources.is_empty() {
                "ДОБАВЬ ИСТОЧНИКИ · выбери папки, которые нужно сохранить"
            } else {
                "ГОТОВО · ручное копирование и автообновление по выбранным папкам"
            };
            panel(ui, "БЫСТРАЯ НАСТРОЙКА", |ui| {
                ui.label(RichText::new(setup_hint).color(if self.config.destination.is_empty() || self.config.sources.is_empty() { Color32::from_rgb(244, 202, 120) } else { green }).size(11.0).strong());
            });
            if self.last_stats.running {
                ui.add_space(6.0);
                panel(ui, "ТЕКУЩАЯ ОПЕРАЦИЯ", |ui| {
                    ui.horizontal(|ui| {
                        ui.spinner();
                        ui.label(RichText::new(&self.last_stats.status).color(Color32::WHITE).size(12.0));
                    });
                    ui.add_space(5.0);
                    if self.last_stats.files_seen > 0 {
                        ui.label(RichText::new(format!("Обработано файлов: {}", self.last_stats.files_seen)).color(muted).size(10.0));
                    }
                    ui.label(RichText::new("Операция выполняется в фоне; точный процент появится после подсчёта общего числа файлов.").color(muted).size(10.0));
                });
            }
            ui.add_space(8.0);
            ui.columns(4, |cols| {
                metric(&mut cols[0], "ФАЙЛЫ СКОПИРОВАНЫ", &self.last_stats.files_copied.to_string(), green);
                metric(&mut cols[1], "ПРОПУЩЕНО БЕЗ ИЗМЕНЕНИЙ", &self.last_stats.files_skipped.to_string(), Color32::WHITE);
                metric(&mut cols[2], "ОШИБКИ", &self.last_stats.errors.to_string(), if self.last_stats.errors > 0 { Color32::from_rgb(255,130,110) } else { muted });
                metric(&mut cols[3], "СКОРОСТЬ", &format!("{}/с", format_bytes(self.last_stats.speed)), Color32::WHITE);
            });
            ui.add_space(10.0);
            ui.columns(2, |cols| {
                panel(&mut cols[0], "НАГРУЗКА ПК", |ui| {
                    resource_bar(ui, "Процессор", self.cpu, format!("{:.0}%", self.cpu), green);
                    resource_bar(ui, "Оперативная память", self.memory, format!("{} / {} ГБ", self.memory_used as f64 / 1_073_741_824.0, self.memory_total as f64 / 1_073_741_824.0), green);
                });
                panel(&mut cols[1], "ПАПКА НАЗНАЧЕНИЯ", |ui| {
                    ui.label(RichText::new(if self.config.destination.is_empty() { "Не выбрана".into() } else { self.config.destination.clone() }).color(if self.config.destination.is_empty() { muted } else { Color32::WHITE }));
                    ui.add_space(5.0);
                    if ui.button("Выбрать папку назначения…").clicked() {
                        if let Some(path) = rfd::FileDialog::new().set_title("Папка резервной копии").pick_folder() {
                            self.config.destination = path.to_string_lossy().to_string(); self.save_config(); self.restart_auto_watchers();
                        }
                    }
                });
            });
            ui.add_space(10.0);
            panel(ui, "ПАПКИ ДЛЯ КОПИРОВАНИЯ", |ui| {
                let mut remove = None;
                let mut toggle_auto: Option<String> = None;
                for (i, path) in self.config.sources.iter().enumerate() {
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("DIR").color(green).monospace().size(10.0));
                        ui.label(RichText::new(path).color(Color32::WHITE).size(12.0));
                        let enabled = self.config.auto_update_sources.iter().any(|p| p.eq_ignore_ascii_case(path));
                        if ui.small_button(if enabled { "Авто: вкл." } else { "Авто: выкл." }).clicked() { toggle_auto = Some(path.clone()); }
                        if ui.small_button("Убрать").clicked() { remove = Some(i); }
                    });
                }
                if let Some(path) = toggle_auto {
                    if let Some(index) = self.config.auto_update_sources.iter().position(|p| p.eq_ignore_ascii_case(&path)) {
                        self.config.auto_update_sources.remove(index);
                    } else {
                        self.config.auto_update_sources.push(path);
                    }
                    self.save_config();
                    self.restart_auto_watchers();
                }
                if let Some(i) = remove {
                    let removed = self.config.sources.remove(i);
                    self.config.auto_update_sources.retain(|p| !p.eq_ignore_ascii_case(&removed));
                    self.save_config();
                    self.restart_auto_watchers();
                }
                ui.horizontal(|ui| {
                    if ui.button("+ Добавить папку").clicked() {
                        if let Some(path) = rfd::FileDialog::new().set_title("Добавить папку-источник").pick_folder() {
                            let value = path.to_string_lossy().to_string();
                            if !self.config.sources.iter().any(|s| Path::new(s) == path) { self.config.sources.push(value); self.save_config(); self.restart_auto_watchers(); }
                        }
                    }
                    ui.label(RichText::new(format!("{} источников", self.config.sources.len())).color(muted).size(11.0));
                });
            });
            ui.add_space(10.0);
            ui.horizontal_wrapped(|ui| {
                if ui.button("Проверить SHA-256").clicked() { self.verify_backup(); }
                if ui.button("Восстановить…").clicked() { self.restore_missing(); }
                if ui.button("Создать ZIP…").clicked() { self.create_zip(); }
                ui.label(RichText::new("РЕЖИМ").color(muted).size(10.0));
                for (value, label) in [("eco","Экономия"),("balanced","Баланс"),("max","Максимальная скорость")] {
                    if ui.selectable_label(self.config.performance_mode == value, label).clicked() { self.config.performance_mode = value.into(); self.save_config(); }
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let (running, cancellable) = self.stats.lock().map(|s| (s.running, s.cancellable)).unwrap_or((true, false));
                    if running && cancellable {
                        if ui.button("■ Остановить").clicked() { self.stop_backup(); }
                    } else if running {
                        ui.label(RichText::new("Операция выполняется…").color(muted).size(11.0));
                    } else if ui.add(egui::Button::new(RichText::new("▶ Начать копирование").color(Color32::from_rgb(13,16,15))).fill(green)).clicked() {
                        self.start_backup();
                    }
                });
            });
            ui.add_space(8.0);
            panel(ui, "ЖУРНАЛ", |ui| {
                for line in &self.log { ui.label(RichText::new(line).color(muted).size(11.0).monospace()); }
                ui.label(RichText::new(&self.last_stats.status).color(if self.last_stats.errors > 0 { Color32::from_rgb(255,130,110) } else { green }).size(11.0));
            });
            ui.add_space(5.0);
            ui.label(RichText::new(format!("German OS Backup Rust Edition v{VERSION} · Windows · локальная работа · без телеметрии")).color(muted).size(10.0));
            });
        });
    }
}


fn unique_sibling_path(path: &Path, tag: &str) -> PathBuf {
    let nonce = SystemTime::now().duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default().as_nanos();
    let file_name = path.file_name().unwrap_or_default().to_string_lossy();
    path.with_file_name(format!("{file_name}.gob-{tag}-{}-{nonce}", std::process::id()))
}

// Copy to a sibling temporary file first. The existing backup is moved aside
// before replacement, and restored if the final rename fails. A cancelled or
// interrupted copy therefore never truncates the last good backup in place.
fn copy_file_atomically(source: &Path, target: &Path, modified: Option<SystemTime>) -> io::Result<u64> {
    if let Some(parent) = target.parent() { fs::create_dir_all(parent)?; }
    let temp = unique_sibling_path(target, "tmp");
    let backup = unique_sibling_path(target, "previous");
    let result = (|| {
        let written = fs::copy(source, &temp)?;
        if let Some(time) = modified {
            filetime::set_file_mtime(&temp, filetime::FileTime::from_system_time(time))?;
        }
        let had_target = target.exists();
        if had_target { fs::rename(target, &backup)?; }
        if let Err(error) = fs::rename(&temp, target) {
            if had_target { let _ = fs::rename(&backup, target); }
            return Err(error);
        }
        if had_target { let _ = fs::remove_file(&backup); }
        Ok(written)
    })();
    if temp.exists() { let _ = fs::remove_file(&temp); }
    result
}

// Restore must never overwrite an existing file. Linking the completed temp
// file into place is atomic and fails safely if another process creates target.
fn restore_file_atomically(source: &Path, target: &Path) -> io::Result<bool> {
    if target.exists() { return Ok(false); }
    if let Some(parent) = target.parent() { fs::create_dir_all(parent)?; }
    let temp = unique_sibling_path(target, "restore-tmp");
    let result = (|| {
        fs::copy(source, &temp)?;
        // This application targets Windows: rename on the same volume is
        // atomic and fails rather than replacing an existing destination.
        match fs::rename(&temp, target) {
            Ok(()) => Ok(true),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => Ok(false),
            Err(error) => Err(error),
        }
    })();
    let _ = fs::remove_file(&temp);
    result
}

fn restore_destination(target: &Path, relative: &Path, backup_root: &Path) -> PathBuf {
    let mut components = relative.components();
    let Some(std::path::Component::Normal(root)) = components.next() else {
        return target.join(relative);
    };
    let root_name = root.to_string_lossy();
    let is_known_folder = |name: &str| -> Option<&'static str> {
        match name.to_lowercase().as_str() {
            "desktop" | "рабочий стол" => Some("Desktop"),
            "documents" | "документы" => Some("Documents"),
            "pictures" | "изображения" | "рисунки" => Some("Pictures"),
            "videos" | "видео" => Some("Videos"),
            "downloads" | "загрузки" => Some("Downloads"),
            _ => None,
        }
    };

    let target_text = target.to_string_lossy();
    let profile_root = std::env::var("USERPROFILE").unwrap_or_default();
    let is_restore_root = target.file_name().is_none()
        || (!profile_root.is_empty() && target_text.eq_ignore_ascii_case(&profile_root));
    if !is_restore_root { return target.join(relative); }

    let (folder, remainder) = if let Some(folder) = is_known_folder(&root_name) {
        let remainder = relative.strip_prefix(root).unwrap_or(Path::new(""));
        (folder, remainder)
    } else if let Some(name) = backup_root.file_name().map(|n| n.to_string_lossy()) {
        if let Some(folder) = is_known_folder(&name) {
            (folder, relative)
        } else {
            return target.join(relative);
        }
    } else {
        return target.join(relative);
    };

    let profile = std::env::var("USERPROFILE").unwrap_or_default();
    let base = match folder {
        "Desktop" => std::env::var("USERPROFILE").map(|p| PathBuf::from(p).join("Desktop")).unwrap_or_else(|_| target.join("Desktop")),
        "Documents" => std::env::var("USERPROFILE").map(|p| PathBuf::from(p).join("Documents")).unwrap_or_else(|_| target.join("Documents")),
        "Pictures" => std::env::var("USERPROFILE").map(|p| PathBuf::from(p).join("Pictures")).unwrap_or_else(|_| target.join("Pictures")),
        "Videos" => std::env::var("USERPROFILE").map(|p| PathBuf::from(p).join("Videos")).unwrap_or_else(|_| target.join("Videos")),
        "Downloads" => PathBuf::from(profile).join("Downloads"),
        _ => return target.join(relative),
    };
    base.join(remainder)
}

fn panel(ui: &mut egui::Ui, title: &str, add: impl FnOnce(&mut egui::Ui)) {
    egui::Frame::new().fill(Color32::from_rgb(17,21,18)).stroke(Stroke::new(1.0, Color32::from_rgb(43,53,45))).inner_margin(egui::Margin::same(13)).show(ui, |ui| {
        ui.label(RichText::new(title).size(10.0).strong().color(Color32::from_rgb(151,240,171)));
        ui.add_space(7.0);
        add(ui);
    });
}
fn metric(ui: &mut egui::Ui, label: &str, value: &str, color: Color32) {
    panel(ui, label, |ui| { ui.label(RichText::new(value).size(24.0).strong().color(color)); });
}
fn resource_bar(ui: &mut egui::Ui, label: &str, value: f32, text: String, color: Color32) {
    ui.horizontal(|ui| { ui.label(RichText::new(label).color(Color32::from_rgb(137,149,140)).size(11.0)); ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| { ui.label(RichText::new(text).color(Color32::WHITE).size(11.0)); }); });
    ui.add(egui::ProgressBar::new((value / 100.0).clamp(0.0,1.0)).fill(color).desired_height(4.0).show_percentage());
    ui.add_space(5.0);
}
fn format_bytes(value: u64) -> String {
    const UNITS: [&str; 4] = ["Б","КБ","МБ","ГБ"];
    let mut n = value as f64; let mut i = 0;
    while n >= 1024.0 && i < UNITS.len()-1 { n /= 1024.0; i += 1; }
    format!("{n:.1} {}", UNITS[i])
}

struct WebUiHost {
    backend: BackupApp,
    window: Option<winit::window::Window>,
    webview: Option<wry::WebView>,
    sender: std::sync::mpsc::Sender<String>,
    receiver: std::sync::mpsc::Receiver<String>,
    last_refresh: std::time::Instant,
}

impl WebUiHost {
    fn new() -> Self {
        let (sender, receiver) = std::sync::mpsc::channel();
        Self {
            backend: BackupApp::new_webview(),
            window: None,
            webview: None,
            sender,
            receiver,
            last_refresh: std::time::Instant::now() - Duration::from_secs(2),
        }
    }

    fn publish_state(&mut self) {
        let Some(webview) = self.webview.as_ref() else { return; };
        self.backend.system.refresh_cpu_usage();
        self.backend.system.refresh_memory();
        self.backend.cpu = self.backend.system.global_cpu_usage();
        self.backend.memory_used = self.backend.system.used_memory();
        self.backend.memory_total = self.backend.system.total_memory();
        self.backend.memory = if self.backend.memory_total > 0 {
            self.backend.memory_used as f32 / self.backend.memory_total as f32 * 100.0
        } else { 0.0 };
        let stats = self.backend.stats.lock().map(|s| s.clone()).unwrap_or_default();
        self.backend.last_stats = stats.clone();
        let status = if stats.running { "copying" }
            else if stats.errors > 0 { "attention" }
            else if stats.files_copied > 0 { "completed" }
            else { "waiting" };
        let headline = if stats.running {
            stats.status.clone()
        } else if stats.errors > 0 {
            "Нужна проверка ошибок".to_string()
        } else if stats.files_copied > 0 {
            "Последняя операция завершена".to_string()
        } else {
            "Ожидаем ручного запуска".to_string()
        };
        let payload = serde_json::json!({
            "status": status,
            "headline": headline,
            "lastScan": if stats.files_copied > 0 { "Последняя операция в этой сессии" } else { "Ещё не завершено" },
            "bytes": stats.bytes_copied,
            "files": stats.files_copied,
            "seen": stats.files_seen,
            "errors": stats.errors,
            "speed": stats.speed,
            "scanning": stats.running,
            "destination": self.backend.config.destination,
            "sourceCount": self.backend.config.sources.len(),
            "sourcePaths": self.backend.config.sources,
            "autoUpdateSources": self.backend.config.auto_update_sources,
            "version": VERSION,
            "cpuPercent": self.backend.cpu,
            "ramUsedGb": self.backend.memory_used as f64 / 1_073_741_824.0,
            "ramTotalGb": self.backend.memory_total as f64 / 1_073_741_824.0,
            "ramPercent": self.backend.memory,
            "performanceMode": self.backend.config.performance_mode
        });
        let _ = webview.evaluate_script(&format!("window.updateStats({payload});"));
    }

    fn handle_action(&mut self, raw: &str) -> bool {
        let request: serde_json::Value = match serde_json::from_str(raw) {
            Ok(value) => value,
            Err(error) => {
                self.notify(&format!("Не удалось обработать команду интерфейса: {error}"));
                return false;
            }
        };
        let action = request.get("action").and_then(|v| v.as_str()).unwrap_or("");
        match action {
            "sources" => {
                if let Some(paths) = rfd::FileDialog::new().set_title("Выбери папки для резервного копирования").pick_folders() {
                    self.backend.config.sources = paths.into_iter()
                        .map(|p| p.to_string_lossy().to_string()).collect();
                    let selected = self.backend.config.sources.clone();
                    self.backend.config.auto_update_sources.retain(|p| {
                        selected.iter().any(|s| s.eq_ignore_ascii_case(p))
                    });
                    self.backend.save_config();
                    self.backend.restart_auto_watchers();
                    self.notify("Источники сохранены. Нажми «Создать / обновить копию», чтобы запустить копирование.");
                }
            }
            "toggleAuto" => {
                if let Some(path) = request.get("path").and_then(|v| v.as_str()) {
                    if self.backend.config.sources.iter().any(|p| p.eq_ignore_ascii_case(path)) {
                        if let Some(index) = self.backend.config.auto_update_sources.iter().position(|p| p.eq_ignore_ascii_case(path)) {
                            self.backend.config.auto_update_sources.remove(index);
                            self.notify("Автообновление отключено. Существующая резервная копия сохранена.");
                        } else {
                            self.backend.config.auto_update_sources.push(path.to_string());
                            self.notify("Автообновление включено для выбранной папки.");
                        }
                        self.backend.save_config();
                        self.backend.restart_auto_watchers();
                    }
                }
            }
            "restore" => self.backend.restore_missing(),
            "verify" => self.backend.verify_backup(),
            "performanceMode" => {
                if let Some(mode) = request.get("mode").and_then(|v| v.as_str()) {
                    if ["eco", "balanced", "max"].contains(&mode) {
                        self.backend.config.performance_mode = mode.to_string();
                        self.backend.save_config();
                        self.notify(match mode {
                            "eco" => "Режим экономии ресурсов включён.",
                            "max" => "Максимальная скорость включена.",
                            _ => "Сбалансированный режим включён.",
                        });
                    }
                }
            }
            "scan" => {
                self.notify("Резервное копирование запущено вручную.");
                self.backend.start_backup();
            }
            "zip" => self.backend.create_zip(),
            "stop" => self.backend.stop_backup(),
            "openDestination" => {
                let path = self.backend.config.destination.clone();
                if path.is_empty() {
                    self.notify("Сначала выбери папку для резервной копии.");
                } else if std::process::Command::new("explorer").arg(path).spawn().is_err() {
                    self.notify("Не удалось открыть папку резервной копии.");
                }
            }
            "chooseDestination" => {
                if let Some(path) = rfd::FileDialog::new().set_title("Выбери отдельную папку для резервных копий").pick_folder() {
                    self.backend.config.destination = path.to_string_lossy().to_string();
                    self.backend.save_config();
                    self.backend.restart_auto_watchers();
                    self.notify("Место хранения сохранено. Новые копии будут записываться в выбранную папку.");
                }
            }
            "log" => {
                let path = self.backend.config_path.parent().unwrap_or(Path::new(".")).join("app.log");
                if let Some(parent) = path.parent() { let _ = fs::create_dir_all(parent); }
                if !path.exists() { let _ = fs::write(&path, "German OS Backup Rust Edition log\n"); }
                if std::process::Command::new("notepad").arg(path).spawn().is_err() {
                    self.notify("Не удалось открыть журнал.");
                }
            }
            "help" => self.notify("Выбери папки-источники, укажи отдельный диск для копии и нажми «Создать / обновить копию». Восстановление не перезаписывает существующие файлы."),
            "checkUpdates" => {
                let _ = std::process::Command::new("cmd").args(["/C", "start", "", RELEASES]).spawn();
                self.notify("Открыта страница официальных релизов GitHub.");
            }
            "exit" => return true,
            _ => self.notify("Это действие пока недоступно в Rust Edition."),
        }
        false
    }

    fn notify(&self, message: &str) {
        if let Some(webview) = self.webview.as_ref() {
            if let Ok(text) = serde_json::to_string(message) {
                let _ = webview.evaluate_script(&format!("window.setNotice({text});"));
            }
        }
    }
}

impl winit::application::ApplicationHandler for WebUiHost {
    fn resumed(&mut self, event_loop: &winit::event_loop::ActiveEventLoop) {
        if self.window.is_some() { return; }
        let window = match event_loop.create_window(
            winit::window::Window::default_attributes()
                .with_title("German OS Backup Rust Edition")
                .with_inner_size(winit::dpi::LogicalSize::new(1180.0, 820.0))
                .with_min_inner_size(winit::dpi::LogicalSize::new(900.0, 680.0))
        ) {
            Ok(window) => window,
            Err(error) => {
                eprintln!("Не удалось создать окно German OS Backup: {error}");
                event_loop.exit();
                return;
            }
        };
        let sender = self.sender.clone();
        let init_script = r#"window.chrome = window.chrome || {}; window.chrome.webview = window.chrome.webview || { postMessage: function(message) { window.ipc.postMessage(typeof message === 'string' ? message : JSON.stringify(message)); } };"#;
        let webview = match wry::WebViewBuilder::new()
            .with_html(include_str!("../dashboard.html"))
            .with_initialization_script(init_script)
            .with_ipc_handler(move |request| {
                let _ = sender.send(request.body().to_string());
            })
            .build(&window) {
                Ok(webview) => webview,
                Err(error) => {
                    eprintln!("Не удалось запустить WebView2: {error}");
                    event_loop.exit();
                    return;
                }
            };
        self.window = Some(window);
        self.webview = Some(webview);
    }

    fn window_event(
        &mut self,
        event_loop: &winit::event_loop::ActiveEventLoop,
        _window_id: winit::window::WindowId,
        event: winit::event::WindowEvent,
    ) {
        if matches!(event, winit::event::WindowEvent::CloseRequested) {
            event_loop.exit();
        }
    }

    fn about_to_wait(&mut self, event_loop: &winit::event_loop::ActiveEventLoop) {
        while let Ok(message) = self.receiver.try_recv() {
            if self.handle_action(&message) {
                event_loop.exit();
                return;
            }
        }
        if self.last_refresh.elapsed() >= Duration::from_secs(1) {
            self.publish_state();
            self.last_refresh = std::time::Instant::now();
        }
        event_loop.set_control_flow(winit::event_loop::ControlFlow::WaitUntil(
            std::time::Instant::now() + Duration::from_millis(100)
        ));
    }
}

fn main() {
    let event_loop = match winit::event_loop::EventLoop::new() {
        Ok(loop_) => loop_,
        Err(error) => {
            eprintln!("Не удалось запустить оконный цикл: {error}");
            return;
        }
    };
    let mut host = WebUiHost::new();
    if let Err(error) = event_loop.run_app(&mut host) {
        eprintln!("Ошибка окна German OS Backup: {error}");
    }
}


#[cfg(test)]
mod regression_tests {
    use super::*;

    fn temp_dir(label: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "gob-test-{}-{}-{}",
            label,
            std::process::id(),
            SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_nanos()
        ));
        fs::create_dir_all(&path).expect("create test directory");
        path
    }

    #[test]
    fn atomic_copy_replaces_old_file_with_complete_content() {
        let dir = temp_dir("atomic-copy");
        let source = dir.join("source.bin");
        let target = dir.join("backup.bin");
        fs::write(&source, vec![0xA5u8; 256 * 1024]).unwrap();
        fs::write(&target, b"old backup").unwrap();

        let written = copy_file_atomically(&source, &target, None).unwrap();

        assert_eq!(written, 256 * 1024);
        assert_eq!(fs::read(&target).unwrap(), fs::read(&source).unwrap());
        assert!(!dir.read_dir().unwrap().any(|e| {
            e.ok().map(|e| e.file_name().to_string_lossy().contains(".gob-tmp-")).unwrap_or(false)
        }));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn atomic_copy_failure_keeps_last_good_backup() {
        let dir = temp_dir("atomic-copy-failure");
        let source = dir.join("missing-source.bin");
        let target = dir.join("backup.bin");
        fs::write(&target, b"last known good backup").unwrap();

        assert!(copy_file_atomically(&source, &target, None).is_err());
        assert_eq!(fs::read(&target).unwrap(), b"last known good backup");
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 1);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn atomic_copy_creates_missing_parent_directories() {
        let dir = temp_dir("atomic-copy-nested");
        let source = dir.join("source.bin");
        let target = dir.join("new").join("nested").join("backup.bin");
        fs::write(&source, b"nested backup").unwrap();

        assert_eq!(copy_file_atomically(&source, &target, None).unwrap(), 13);
        assert_eq!(fs::read(&target).unwrap(), b"nested backup");
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn restore_keeps_existing_user_data_when_source_is_missing() {
        let dir = temp_dir("restore-missing-source");
        let source = dir.join("missing-backup.txt");
        let target = dir.join("current.txt");
        fs::write(&target, b"keep me").unwrap();

        assert!(restore_file_atomically(&source, &target).is_err());
        assert_eq!(fs::read(&target).unwrap(), b"keep me");
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn restore_maps_documents_folder_from_drive_root_to_profile() {
        if !cfg!(windows) { return; }
        let profile = std::env::var("USERPROFILE").expect("Windows user profile");
        let target = PathBuf::from(format!("{}\\\\", std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into())));
        let relative = Path::new("Documents").join("School").join("notes.txt");
        let backup = PathBuf::from(r"D:\Backup\Documents");

        let actual = restore_destination(&target, &relative, &backup);
        let expected = PathBuf::from(profile).join("Documents").join("School").join("notes.txt");
        assert_eq!(actual, expected);
    }

    #[test]
    fn restore_does_not_overwrite_existing_file() {
        let dir = temp_dir("restore-no-overwrite");
        let source = dir.join("backup.txt");
        let target = dir.join("current.txt");
        fs::write(&source, b"restored").unwrap();
        fs::write(&target, b"user data").unwrap();

        let restored = restore_file_atomically(&source, &target).unwrap();

        assert!(!restored);
        assert_eq!(fs::read(&target).unwrap(), b"user data");
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn restore_creates_missing_file_with_exact_content() {
        let dir = temp_dir("restore-missing");
        let source = dir.join("backup.txt");
        let target = dir.join("nested").join("restored.txt");
        fs::write(&source, b"important save").unwrap();

        assert!(restore_file_atomically(&source, &target).unwrap());
        assert_eq!(fs::read(&target).unwrap(), b"important save");
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn restore_maps_pictures_folder_from_drive_root_to_profile() {
        if !cfg!(windows) { return; }
        let profile = std::env::var("USERPROFILE").expect("Windows user profile");
        let target = PathBuf::from(format!("{}\\", std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into())));
        let relative = Path::new("Pictures").join("Holiday").join("photo.jpg");
        let backup = PathBuf::from(r"D:\Backup\Pictures");

        let actual = restore_destination(&target, &relative, &backup);
        let expected = PathBuf::from(profile).join("Pictures").join("Holiday").join("photo.jpg");
        assert_eq!(actual, expected);
    }

    #[test]
    fn restore_maps_selected_pictures_backup_folder_to_profile() {
        if !cfg!(windows) { return; }
        let profile = std::env::var("USERPROFILE").expect("Windows user profile");
        let target = PathBuf::from(format!("{}\\", std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into())));
        let relative = Path::new("photo.jpg");
        let backup = PathBuf::from(r"D:\Backup\Pictures");

        let actual = restore_destination(&target, relative, &backup);
        let expected = PathBuf::from(profile).join("Pictures").join("photo.jpg");
        assert_eq!(actual, expected);
    }
}

