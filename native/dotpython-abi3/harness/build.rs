// Link the harness against the bridge cdylib (as the C++ Catch2 harness did) so bridge symbols are
// in the process; dlopened fixtures resolve their undefined Py* symbols against it.
fn main() {
    let manifest = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    let profile = std::env::var("PROFILE").unwrap();
    let target_dir = format!("{manifest}/../target/{profile}");
    println!("cargo:rustc-link-search=native={target_dir}");
    println!("cargo:rustc-link-search=native={target_dir}/deps");
    println!("cargo:rustc-link-lib=dylib=dotpython_abi3");
    // Absolute rpath for running out of the Cargo target dir during development...
    println!("cargo:rustc-link-arg=-Wl,-rpath,{target_dir}");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{target_dir}/deps");
    // ...and a loader-relative rpath so the staged binary finds the staged bridge beside it.
    if cfg!(target_os = "macos") {
        println!("cargo:rustc-link-arg=-Wl,-rpath,@loader_path");
    } else {
        println!("cargo:rustc-link-arg=-Wl,-rpath,$ORIGIN");
    }
}
