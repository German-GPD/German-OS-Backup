use std::fs;
use std::io;
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static NEXT_TEMP: AtomicU64 = AtomicU64::new(1);

#[link(name = "kernel32")]
extern "system" {
    fn MoveFileExW(existing: *const u16, replacement: *const u16, flags: u32) -> i32;
    fn SetLastError(error: u32);
}

const MOVEFILE_REPLACE_EXISTING: u32 = 0x1;
const MOVEFILE_WRITE_THROUGH: u32 = 0x8;

fn wide_path(path: &PathBuf) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

/// Copies one file to a temporary sibling and replaces the destination only
/// after the full copy succeeds. Returns 0 on success, otherwise a Win32 code.
#[no_mangle]
pub unsafe extern "system" fn gob_copy_file_utf16(source: *const u16, target: *const u16) -> i32 {
    if source.is_null() || target.is_null() {
        unsafe { SetLastError(87); }
        return 87;
    }

    let source_len = (0..).take_while(|&i| *source.add(i) != 0).count();
    let target_len = (0..).take_while(|&i| *target.add(i) != 0).count();
    if source_len == 0 || target_len == 0 {
        SetLastError(87);
        return 87;
    }

    let src = PathBuf::from(std::ffi::OsString::from_wide(std::slice::from_raw_parts(source, source_len)));
    let dst = PathBuf::from(std::ffi::OsString::from_wide(std::slice::from_raw_parts(target, target_len)));
    let id = NEXT_TEMP.fetch_add(1, Ordering::Relaxed);
    let mut temp_name = dst.as_os_str().to_os_string();
    temp_name.push(format!(".gob-rust-{}-{}", std::process::id(), id));
    let temp = PathBuf::from(temp_name);

    let result = (|| -> io::Result<()> {
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(&src, &temp)?;
        let temp_wide = wide_path(&temp);
        let dst_wide = wide_path(&dst);
        let moved = unsafe { MoveFileExW(temp_wide.as_ptr(), dst_wide.as_ptr(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) };
        if moved == 0 {
            let code = std::io::Error::last_os_error().raw_os_error().unwrap_or(1) as u32;
            unsafe { SetLastError(code); }
            let _ = fs::remove_file(&temp);
            return Err(io::Error::from_raw_os_error(code as i32));
        }
        Ok(())
    })();

    match result {
        Ok(()) => 0,
        Err(error) => {
            let code = error.raw_os_error().unwrap_or(1).unsigned_abs();
            SetLastError(code);
            let _ = fs::remove_file(&temp);
            code as i32
        }
    }
}


#[cfg(test)]
mod tests {
    use super::gob_copy_file_utf16;
    use std::{
        fs,
        os::windows::ffi::OsStrExt,
        path::Path,
        time::{SystemTime, UNIX_EPOCH},
    };

    fn wide(path: &Path) -> Vec<u16> {
        path.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
    }

    #[test]
    fn copies_file_and_replaces_destination() {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock must be after Unix epoch")
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "gob-native-engine-test-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&root).expect("create test directory");
        let source = root.join("source-save.dat");
        let target = root.join("nested").join("backup-save.dat");
        let payload = b"German OS Backup native Rust test payload";
        fs::write(&source, payload).expect("write source");
        fs::create_dir_all(target.parent().expect("target parent")).expect("create target parent");
        fs::write(&target, b"old data").expect("write old destination");
        let source_wide = wide(&source);
        let target_wide = wide(&target);

        // SAFETY: both buffers are NUL-terminated UTF-16 paths and stay alive
        // for the duration of the native call.
        let result = unsafe {
            gob_copy_file_utf16(source_wide.as_ptr(), target_wide.as_ptr())
        };

        assert_eq!(result, 0, "native copy must succeed");
        assert_eq!(fs::read(&target).expect("read destination"), payload);
        let _ = fs::remove_dir_all(root);
    }
}
