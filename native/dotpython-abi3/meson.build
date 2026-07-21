project(
  'dotpython-abi3',
  ['c', 'cpp'],
  version: '0.1.0',
  meson_version: '>=1.9.0',
  default_options: [
    'c_std=c11',
    'cpp_std=c++20',
    # CPython slots and POSIX dlsym require function/object pointer interop that is not ISO C.
    'warning_level=2',
    'werror=true',
    'b_lundef=true',
  ],
)

host_system = host_machine.system()
if host_system != 'darwin' and host_system != 'linux'
  error('dotpython-abi3 currently supports only macOS and Linux')
endif

if host_system == 'darwin'
  xcrun = find_program('xcrun')
  macos_sdk_path = run_command(
    xcrun,
    '--show-sdk-path',
    check: true,
  ).stdout().strip()
  add_project_arguments('-isysroot', macos_sdk_path, language: ['c', 'cpp'])
endif

if get_option('b_sanitize') != 'none'
  add_project_arguments('-fno-omit-frame-pointer', language: ['c', 'cpp'])
endif

loader_rpath = host_system == 'darwin' ? '@loader_path' : '$ORIGIN'
include_directory = include_directories('include')
c_compiler = meson.get_compiler('c')
dynamic_loader_dependency = c_compiler.find_library(
  'dl',
  required: host_system == 'linux',
)
catch2_project = subproject(
  'catch2',
  default_options: [
    'install=false',
    'tests=false',
    'werror=false',
  ],
)
catch2_dependency = catch2_project.get_variable('catch2_dep').as_system('system')

bridge = shared_library(
  'dotpython_abi3',
  'src/dotpython_abi3.cpp',
  cpp_args: ['-fno-exceptions', '-fno-rtti'],
  include_directories: include_directory,
  gnu_symbol_visibility: 'hidden',
  build_rpath: loader_rpath,
  install_rpath: loader_rpath,
  install: false,
)

fixture = shared_module(
  'dotpython_fixture',
  'fixture/dotpython_fixture.c',
  include_directories: include_directory,
  link_with: bridge,
  name_prefix: '',
  name_suffix: 'abi3.so',
  gnu_symbol_visibility: 'hidden',
  build_rpath: loader_rpath,
  install_rpath: loader_rpath,
  install: false,
)

failure_fixture = shared_module(
  'dotpython_fixture_failure',
  'fixture/dotpython_fixture_init_failure.c',
  include_directories: include_directory,
  link_with: bridge,
  name_prefix: '',
  name_suffix: 'abi3.so',
  gnu_symbol_visibility: 'hidden',
  build_rpath: loader_rpath,
  install_rpath: loader_rpath,
  install: false,
)

native_fixture_test = executable(
  'native_fixture_test',
  'test/native_fixture_test.cpp',
  include_directories: include_directory,
  link_with: bridge,
  dependencies: [dynamic_loader_dependency, catch2_dependency],
  gnu_symbol_visibility: 'hidden',
  build_rpath: loader_rpath,
  install_rpath: loader_rpath,
  install: false,
)

native_anyver_test = executable(
  'native_anyver_test',
  'test/native_anyver_test.cpp',
  include_directories: include_directory,
  link_with: bridge,
  dependencies: [dynamic_loader_dependency, catch2_dependency],
  gnu_symbol_visibility: 'hidden',
  build_rpath: loader_rpath,
  install_rpath: loader_rpath,
  install: false,
)

configure_file(
  input: 'symbol-manifest.json',
  output: 'symbol-manifest.json',
  copy: true,
)
configure_file(
  input: 'anyver-symbol-manifest.json',
  output: 'anyver-symbol-manifest.json',
  copy: true,
)

check_manifest = find_program('check-symbol-manifest.sh')
check_binary_symbols = find_program('check-binary-symbols.sh')

test('symbol-manifests', check_manifest)
test(
  'fixture-stable-abi-imports',
  check_binary_symbols,
  args: [fixture, 'imports'],
)
test(
  'fixture-exports',
  check_binary_symbols,
  args: [fixture, 'fixture-exports'],
)
test(
  'failure-fixture-stable-abi-imports',
  check_binary_symbols,
  args: [failure_fixture, 'failure-imports'],
)
test(
  'failure-fixture-exports',
  check_binary_symbols,
  args: [failure_fixture, 'fixture-exports'],
)
test(
  'bridge-exports',
  check_binary_symbols,
  args: [bridge, 'bridge-exports'],
)
test(
  'fixture-lifecycle-and-failure',
  native_fixture_test,
  args: [fixture, failure_fixture],
)

anyver_module = get_option('anyver_module')
if anyver_module != ''
  test(
    'pinned-anyver-native-harness',
    native_anyver_test,
    args: [anyver_module],
  )
endif
