// Restrict the cdylib export table to exactly the DotPython-owned C ABI, so the checked-in
// symbol manifests (`bridge-exports.txt`) match `nm` output byte-for-byte. Without this the
// linker would also export Rust runtime and allocator symbols.
fn main() {
    let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    // The strict list forces every entry as an "initial undefine", so it only links once ALL
    // exports are defined. It is off during the incremental port and on for the staged artifact.
    println!("cargo:rerun-if-env-changed=DP_ABI3_STRICT_EXPORTS");
    if std::env::var_os("DP_ABI3_STRICT_EXPORTS").is_none() {
        return;
    }
    if cfg!(target_os = "macos") {
        println!("cargo:rustc-link-arg=-Wl,-exported_symbols_list,{dir}/exports.macos.txt");
        println!("cargo:rerun-if-changed=exports.macos.txt");
    } else if cfg!(target_os = "linux") {
        println!("cargo:rustc-link-arg=-Wl,--version-script={dir}/exports.linux.map");
        println!("cargo:rerun-if-changed=exports.linux.map");
    }
}
