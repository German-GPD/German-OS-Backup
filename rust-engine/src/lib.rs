use std::{
    ffi::CStr,
    fs::{self, File},
    os::raw::c_char,
    path::Path,
    ptr,
};

/// Copies a UTF-8 path to a temporary destination used by the managed backup
/// engine. Returns 0 on success, 1 for invalid arguments, 2 for a copy error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gob_copy_file(
    source: *const c_char,
    target: *const c_char,
    bytes_copied: *mut u64,
) -> i32 {
    if source.is_null() || target.is_null() || bytes_copied.is_null() {
        return 1;
    }
    // SAFETY: callers pass valid NUL-terminated UTF-8 strings and a writable
    // pointer to a u64 for the duration of this call.
    let source = unsafe { CStr::from_ptr(source) };
    let target = unsafe { CStr::from_ptr(target) };
    let source = match source.to_str() {
        Ok(value) => value,
        Err(_) => return 1,
    };
    let target = match target.to_str() {
        Ok(value) => value,
        Err(_) => return 1,
    };
    let result = (|| -> std::io::Result<u64> {
        if let Some(parent) = Path::new(target).parent() {
            fs::create_dir_all(parent)?;
        }
        let copied = fs::copy(source, target)?;
        let file = File::open(target)?;
        file.sync_all()?;
        Ok(copied)
    })();
    match result {
        Ok(bytes) => {
            // SAFETY: validated non-null above; the caller owns this output.
            unsafe { ptr::write(bytes_copied, bytes) };
            0
        }
        Err(_) => {
            let _ = fs::remove_file(target);
            2
        }
    }
}
