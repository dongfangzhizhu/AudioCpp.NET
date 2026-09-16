#!/usr/bin/env bash
set -u
fail=0
ok() { echo "[OK]   $1"; }
bad() { echo -e "[FAIL] $1\n       $2"; fail=$((fail+1)); }
echo 'Debian CUDA GPU build environment (read-only check)'
command -v nvidia-smi >/dev/null && nvidia-smi --query-gpu=name,compute_cap --format=csv,noheader && ok 'NVIDIA WSL driver/GPU' || bad 'NVIDIA driver/GPU' 'Install a Windows NVIDIA driver with WSL2 CUDA support; verify nvidia-smi.'
command -v nvcc >/dev/null && nvcc --version >/dev/null && ok 'nvcc' || bad 'nvcc' 'Install the complete Linux CUDA Toolkit, not only the driver.'
command -v cmake >/dev/null && ok 'CMake' || bad 'CMake' 'Install CMake 3.20+.'
command -v g++ >/dev/null && ok 'g++' || bad 'g++' 'Install build-essential.'
cuda=${CUDAToolkit_ROOT:-${CUDA_HOME:-/usr/local/cuda}}
[[ -f "$cuda/include/cuda_runtime.h" ]] && ok "CUDA headers: $cuda/include/cuda_runtime.h" || bad 'CUDA headers' "Install CUDA development headers under $cuda/include."
[[ -e "$cuda/lib64/libcudart.so" || -e "$cuda/lib/libcudart.so" ]] && ok 'CUDA runtime development library (libcudart.so)' || bad 'CUDA runtime development library' "Install libcudart development files and ensure $cuda/lib64 or $cuda/lib contains libcudart.so."
echo
echo "Result: $fail issue(s). Configure CMake with -DCUDAToolkit_ROOT=$cuda -DCMAKE_CUDA_ARCHITECTURES=89."
echo "Export LD_LIBRARY_PATH=$cuda/lib64 before running the built program."
exit "$fail"