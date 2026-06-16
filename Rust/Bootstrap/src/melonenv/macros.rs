#[macro_export]
macro_rules! debug_enabled {
    () => {{
        $crate::melonenv::macros::debug_enabled_cached()
    }};
}

// Cached debug flag. On Android the process is launched by the system, so std::env::args() never
// contains "--melonloader.debug" and debug logging could never be turned on. In addition to the
// build flag / cmdline arg, honor a marker file ("debug" in the MelonLoader base dir, or
// /sdcard/melonloader.debug) so debug logging can be enabled on-device (adb shell touch ...).
// Computed once and cached (debug! is on hot paths, so don't stat per-call).
pub fn debug_enabled_cached() -> bool {
    use std::sync::atomic::{AtomicI8, Ordering};
    static STATE: AtomicI8 = AtomicI8::new(-1);

    let cached = STATE.load(Ordering::Relaxed);
    if cached >= 0 {
        return cached == 1;
    }

    let enabled = cfg!(debug_assertions)
        || std::env::args().any(|a| a == "--melonloader.debug")
        || std::path::Path::new("/sdcard/melonloader.debug").exists()
        || crate::melonenv::paths::BASE_DIR
            .clone()
            .join("MelonLoader")
            .join("debug")
            .exists();

    STATE.store(if enabled { 1 } else { 0 }, Ordering::Relaxed);
    enabled
}

#[macro_export]
macro_rules! should_set_title {
    () => {{
        let args: Vec<String> = std::env::args().collect();
        !args.contains(&"--melonloader.consoledst".to_string())
    }};
}

#[macro_export]
macro_rules! console_on_top {
    () => {{
        let args: Vec<String> = std::env::args().collect();
        args.contains(&"--melonloader.consoleontop".to_string())
    }};
}

#[macro_export]
macro_rules! hide_console {
    () => {{
        let args: Vec<String> = std::env::args().collect();
        args.contains(&"--melonloader.hideconsole".to_string())
    }};
}