#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use directories::ProjectDirs;
use eframe::egui::{self, Color32, RichText, Stroke, Vec2};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs,
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
    destination: String,
    sources: Vec<String>,
    performance_mode: String,
}
impl Default for Config {
    fn default() -> Self {
        let home = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".into());
        let home = PathBuf::from(home);
        let mut sources = Vec::new();
        for name in ["Desktop", "Documents", "Pictures", "Videos"] {
            let p = home.join(name);
            if p.is_dir() { sources.push(p.to_string_lossy().to_string()); }
        }
        Self { destination: String::new(), sources, performance_mode: "balanced".into() }
    }
}

#[derive(Default, Clone)]
struct Stats {
    running: bool,
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

        let project = ProjectDirs::from("com", "German-GPD", "GermanOSBackup")
            .expect("Windows user data directory is unavailable");
        let config_path = project.config_dir().join("config.json");
        let config = fs::read_to_string(&config_path)
            .ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default();
        let stats = Arc::new(Mutex::new(Stats { status: "Готово к работе".into(), ..Default::default() }));
        Self { config, config_path, stats, cancel: Arc::new(AtomicBool::new(false)),
            system: System::new(), cpu: 0.0, memory: 0.0, memory_used: 0, memory_total: 0,
            last_stats: Stats::default(), last_tick: std::time::Instant::now(), log: vec![
                format!("German OS Backup {VERSION} запущен"),
                "Данные хранятся локально; телеметрии нет".into()
            ] }
    }

    fn save_config(&mut self) {
        if let Some(parent) = self.config_path.parent() { let _ = fs::create_dir_all(parent); }
        if let Ok(json) = serde_json::to_string_pretty(&self.config) {
            let temp = self.config_path.with_extension("json.tmp");
            if fs::write(&temp, json).is_ok() { let _ = fs::rename(temp, &self.config_path); }
        }
    }

    fn add_log(&mut self, line: impl Into<String>) {
        self.log.push(line.into());
        if self.log.len() > 8 { self.log.remove(0); }
    }

    fn start_backup(&mut self) {
        if self.config.destination.trim().is_empty() {
            self.add_log("Сначала выбери папку назначения");
            return;
        }
        if self.config.sources.is_empty() {
            self.add_log("Сначала добавь папки-источники");
            return;
        }
        let dest = PathBuf::from(&self.config.destination);
        if !dest.is_absolute() {
            self.add_log("Путь назначения должен быть абсолютным");
            return;
        }
        for source in &self.config.sources {
            let src = PathBuf::from(source);
            if src == dest || src.starts_with(&dest) || dest.starts_with(&src) {
                self.add_log("Папка назначения пересекается с источником — копирование отменено");
                return;
            }
        }
        self.save_config();
        self.cancel.store(false, Ordering::SeqCst);
        let cancel = self.cancel.clone();
        let stats = self.stats.clone();
        let sources = self.config.sources.clone();
        let destination = dest;
        if let Ok(mut s) = stats.lock() {
            *s = Stats { running: true, status: "Подготовка к копированию…".into(), started: Some(SystemTime::now()), ..Default::default() };
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
                for entry in WalkDir::new(&source).follow_links(false).into_iter().filter_map(Result::ok) {
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
                    match fs::copy(path, &target) {
                        Ok(n) => {
                            total_bytes += n; window_bytes += n;
                            if let Ok(mut s) = stats.lock() { s.files_copied += 1; s.bytes_copied = total_bytes; }
                        },
                        Err(_) => if let Ok(mut s) = stats.lock() { s.errors += 1; },
                    }
                    if last_speed.elapsed() >= Duration::from_secs(1) {
                        if let Ok(mut s) = stats.lock() { s.speed = window_bytes / last_speed.elapsed().as_secs().max(1); }
                        window_bytes = 0; last_speed = std::time::Instant::now();
                    }
                }
            }
            if let Ok(mut s) = stats.lock() {
                s.running = false; s.speed = 0;
                s.status = if cancel.load(Ordering::SeqCst) { "Остановлено пользователем".into() }
                    else { "Копирование завершено".into() };
            }
        });
    }

    fn verify_backup(&mut self) {
        if self.config.destination.trim().is_empty() {
            self.add_log("Сначала выбери папку резервной копии");
            return;
        }
        let destination = PathBuf::from(&self.config.destination);
        let sources = self.config.sources.clone();
        let stats = self.stats.clone();
        if let Ok(mut s) = stats.lock() { s.running = true; s.status = "Глубокая проверка SHA-256…".into(); }
        self.add_log("Запущена глубокая проверка SHA-256");
        thread::spawn(move || {
            let mut checked = 0u64; let mut missing = 0u64; let mut different = 0u64;
            for source_text in sources {
                let source = PathBuf::from(&source_text);
                if !source.is_dir() { continue; }
                let root_name = source.file_name().map(|n| n.to_os_string()).unwrap_or_else(|| "Backup".into());
                let target_root = destination.join(root_name);
                for entry in WalkDir::new(&source).follow_links(false).into_iter().filter_map(Result::ok) {
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
                s.running = false;
                s.status = format!("Проверка завершена · проверено {checked}, отсутствует {missing}, различается {different}");
            }
        });
    }

    fn restore_missing(&mut self) {
        let backup = rfd::FileDialog::new().set_title("Выбери корень резервной копии").pick_folder();
        let Some(backup) = backup else { return; };
        let target = rfd::FileDialog::new().set_title("Куда восстановить отсутствующие файлы").pick_folder();
        let Some(target) = target else { return; };
        if backup == target || target.starts_with(&backup) {
            self.add_log("Папка восстановления не должна находиться внутри резервной копии");
            return;
        }
        let stats = self.stats.clone();
        if let Ok(mut s) = stats.lock() { s.running = true; s.status = "Восстановление отсутствующих файлов…".into(); }
        self.add_log("Запущено восстановление без перезаписи существующих файлов");
        thread::spawn(move || {
            let mut restored = 0u64; let mut skipped = 0u64; let mut errors = 0u64;
            for entry in WalkDir::new(&backup).follow_links(false).into_iter().filter_map(Result::ok) {
                if !entry.file_type().is_file() { continue; }
                let rel = match entry.path().strip_prefix(&backup) { Ok(p) => p, Err(_) => continue };
                let out = target.join(rel);
                if out.exists() { skipped += 1; continue; }
                if let Some(parent) = out.parent() { if fs::create_dir_all(parent).is_err() { errors += 1; continue; } }
                match fs::copy(entry.path(), &out) { Ok(_) => restored += 1, Err(_) => errors += 1 }
            }
            if let Ok(mut s) = stats.lock() {
                s.running = false;
                s.status = format!("Восстановлено {restored} · пропущено {skipped} · ошибок {errors}");
                s.files_copied += restored; s.errors += errors;
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
                    ui.label(RichText::new("GERMAN OS BACKUP").size(17.0).strong().color(Color32::WHITE));
                    ui.label(RichText::new(format!("ЛОКАЛЬНОЕ РЕЗЕРВНОЕ КОПИРОВАНИЕ · v{VERSION}")).size(10.0).color(muted));
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button("Открыть релизы ↗").clicked() { ctx.open_url(egui::OpenUrl::new_tab(RELEASES)); }
                    ui.label(RichText::new("●").color(green));
                });
            });
        });

        egui::CentralPanel::default().frame(egui::Frame::new().fill(Color32::from_rgb(13,16,15)).inner_margin(egui::Margin::symmetric(22,10))).show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.heading(RichText::new("Состояние системы").size(21.0).color(Color32::WHITE));
                ui.label(RichText::new(if self.last_stats.running { "● КОПИРОВАНИЕ" } else { "● ОЖИДАНИЕ" }).color(if self.last_stats.running { green } else { muted }).size(11.0));
            });
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
                            self.config.destination = path.to_string_lossy().to_string(); self.save_config();
                        }
                    }
                });
            });
            ui.add_space(10.0);
            panel(ui, "ПАПКИ ДЛЯ КОПИРОВАНИЯ", |ui| {
                let mut remove = None;
                for (i, path) in self.config.sources.iter().enumerate() {
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("DIR").color(green).monospace().size(10.0));
                        ui.label(RichText::new(path).color(Color32::WHITE).size(12.0));
                        if ui.small_button("Убрать").clicked() { remove = Some(i); }
                    });
                }
                if let Some(i) = remove { self.config.sources.remove(i); self.save_config(); }
                ui.horizontal(|ui| {
                    if ui.button("+ Добавить папку").clicked() {
                        if let Some(path) = rfd::FileDialog::new().set_title("Добавить папку-источник").pick_folder() {
                            let value = path.to_string_lossy().to_string();
                            if !self.config.sources.iter().any(|s| Path::new(s) == path) { self.config.sources.push(value); self.save_config(); }
                        }
                    }
                    ui.label(RichText::new(format!("{} источников", self.config.sources.len())).color(muted).size(11.0));
                });
            });
            ui.add_space(10.0);
            ui.horizontal(|ui| {
                if ui.button("Проверить SHA-256").clicked() { self.verify_backup(); }
                if ui.button("Восстановить…").clicked() { self.restore_missing(); }
                ui.label(RichText::new("РЕЖИМ").color(muted).size(10.0));
                for (value, label) in [("eco","Экономия"),("balanced","Баланс"),("max","Максимальная скорость")] {
                    if ui.selectable_label(self.config.performance_mode == value, label).clicked() { self.config.performance_mode = value.into(); self.save_config(); }
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if self.last_stats.running {
                        if ui.button("■ Остановить").clicked() { self.stop_backup(); }
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
            ui.label(RichText::new(format!("German OS Backup v{VERSION} · Windows · локальная работа · без телеметрии")).color(muted).size(10.0));
        });
    }
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

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default().with_inner_size([1180.0, 840.0]).with_min_inner_size([880.0, 700.0]),
        ..Default::default()
    };
    eframe::run_native("German OS Backup", options, Box::new(|cc| Ok(Box::new(BackupApp::new(cc)))))
}
