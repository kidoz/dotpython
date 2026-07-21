// The fixture is a Stable-ABI *consumer*: it exports only its two entry points and imports the
// bridge's `Py*` symbols, resolved at load time (the harness loads the bridge with global scope).
fn main() {
    let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    if cfg!(target_os = "macos") {
        println!("cargo:rustc-link-arg=-Wl,-exported_symbols_list,{dir}/exports.macos.txt");
        // Allow undefined bridge symbols; resolved from the globally-loaded bridge at dlopen.
        println!("cargo:rustc-link-arg=-undefined");
        println!("cargo:rustc-link-arg=dynamic_lookup");
        println!("cargo:rerun-if-changed=exports.macos.txt");
    } else if cfg!(target_os = "linux") {
        println!("cargo:rustc-link-arg=-Wl,--version-script={dir}/exports.linux.map");
        println!("cargo:rerun-if-changed=exports.linux.map");
    }
}
