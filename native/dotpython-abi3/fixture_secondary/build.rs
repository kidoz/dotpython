fn main() {
    let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    if cfg!(target_os = "macos") {
        println!("cargo:rustc-link-arg=-Wl,-exported_symbols_list,{dir}/exports.macos.txt");
        println!("cargo:rustc-link-arg=-undefined");
        println!("cargo:rustc-link-arg=dynamic_lookup");
        println!("cargo:rerun-if-changed=exports.macos.txt");
    } else if cfg!(target_os = "linux") {
        println!("cargo:rustc-link-arg=-Wl,--version-script={dir}/exports.linux.map");
        println!("cargo:rerun-if-changed=exports.linux.map");
    }
}
