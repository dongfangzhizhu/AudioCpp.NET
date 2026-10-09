#!/usr/bin/env bash
# Read-only preflight for the Vulkan backend on Debian/WSL.
#
# The Vulkan split matters and is easy to get wrong: glslc is a BUILD-time requirement
# (ggml-vulkan compiles its shaders ahead of the link, and CMake asks for
# `find_package(Vulkan COMPONENTS glslc REQUIRED)`), while the loader
# (libvulkan.so.1) is a RUN-time requirement supplied by the GPU driver. Having one
# without the other is the usual failure: the configure dies on a missing glslc even
# though vulkaninfo works fine.
set -u
fail=0
ok()  { echo "[OK]   $1"; }
bad() { echo -e "[FAIL] $1\n       $2"; fail=$((fail+1)); }

echo 'Debian Vulkan build environment (read-only check)'

command -v cmake >/dev/null && ok "CMake ($(cmake --version | head -1))" \
  || bad 'CMake' 'Install CMake 3.20+.'

# Build-time: the shader compiler. Package name differs by release; both provide glslc.
if command -v glslc >/dev/null; then
  ok "glslc ($(glslc --version | head -1))"
else
  bad 'glslc (shader compiler)' \
      'apt-get install -y glslc   (older releases: glslang-tools). Required at build time by ggml-vulkan.'
fi

# Build-time: the Vulkan headers. A full SDK also carries them; the distro package is enough.
if [[ -n "${VULKAN_SDK:-}" && -f "$VULKAN_SDK/include/vulkan/vulkan.h" ]]; then
  ok "Vulkan headers (VULKAN_SDK=$VULKAN_SDK)"
elif [[ -f /usr/include/vulkan/vulkan.h ]]; then
  ok 'Vulkan headers (/usr/include/vulkan/vulkan.h)'
else
  bad 'Vulkan headers' \
      'apt-get install -y libvulkan-dev, or point VULKAN_SDK at an unpacked LunarG SDK and pass -DVULKAN_SDK=$VULKAN_SDK.'
fi

# Run-time only: the loader. Absent here does NOT block the build, but the shim will
# fail to load, so it is worth reporting.
if ldconfig -p 2>/dev/null | grep -q 'libvulkan\.so\.1'; then
  ok 'Vulkan loader (libvulkan.so.1) present'
else
  bad 'Vulkan loader (libvulkan.so.1)' \
      'apt-get install -y libvulkan1. Needed to RUN the shim, not to build it; without a GPU/lavapipe ICD the e2e cells cannot pass.'
fi

# Whether anything can actually execute a compute queue.
if command -v vulkaninfo >/dev/null; then
  if vulkaninfo --summary >/dev/null 2>&1; then
    ok "a Vulkan device is visible ($(vulkaninfo --summary 2>/dev/null | grep -m1 'deviceName' || echo 'summary ok'))"
  else
    bad 'Vulkan device' \
        'vulkaninfo ran but reported no usable device. On a headless host install lavapipe (mesa-vulkan-drivers) to get a software ICD for CI.'
  fi
else
  echo '[WARN] vulkaninfo not installed (package vulkan-tools); cannot confirm a device is present.'
fi

echo
echo "Result: $fail issue(s)."
echo 'Configure with -DAUDIOCPP_BACKEND=vulkan; add -DVULKAN_SDK=<sdk> only when using a'
echo 'custom SDK instead of the distro packages.'
exit "$fail"