use crate::{ast::json::to_pretty_json, Makefile};
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Parse Makefile text and return its JSON representation.
///
/// The returned string is owned by Rust. Release it with
/// `makefile_free_string`. Returns null for invalid UTF-8, JSON conversion
/// failure, or a caught panic.
#[no_mangle]
pub unsafe extern "C" fn makefile_to_json(input: *const c_char) -> *mut c_char {
    if input.is_null() {
        return std::ptr::null_mut();
    }

    catch_unwind(AssertUnwindSafe(|| {
        // The caller must pass a valid, null-terminated UTF-8 string.
        let input = unsafe { CStr::from_ptr(input) }.to_str().ok()?;

        // Relaxed parsing keeps a tree even if the Makefile has syntax errors.
        let (makefile, _parse_errors) = Makefile::from_str_relaxed(input);
        let json = to_pretty_json(&makefile).ok()?;

        CString::new(json).ok().map(CString::into_raw)
    }))
        .ok()
        .flatten()
        .unwrap_or(std::ptr::null_mut())
}

/// Free a string returned by `makefile_to_json`.
///
/// Pass the pointer returned by `makefile_to_json`; do not call this twice
/// for the same pointer.
#[no_mangle]
pub unsafe extern "C" fn makefile_free_string(value: *mut c_char) {
    if !value.is_null() {
        // `value` must have been returned by `makefile_to_json`.
        drop(unsafe { CString::from_raw(value) });
    }
}