cmake_minimum_required(VERSION 3.20)
project(AudioCppDotNetNative LANGUAGES C CXX)

set(AUDIOCPP_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../audio.cpp" CACHE PATH "Pinned audio.cpp checkout")
set(AUDIOCPP_BACKEND "cpu" CACHE STRING "cpu|cuda|vulkan|hip|metal")
set(AUDIOCPP_MODEL_SET "custom" CACHE STRING "audio.cpp model set")
set(AUDIOCPP_MODELS "qwen3_tts" CACHE STRING "Models for the custom model set")
set(AUDIOCPP_PINNED_COMMIT "78d47706c30ef215ba9ad3559baff309efeb5260")

execute_process(
  COMMAND git -C "${AUDIOCPP_SRC}" rev-parse HEAD
  OUTPUT_VARIABLE AUDIOCPP_ACTUAL_COMMIT
  OUTPUT_STRIP_TRAILING_WHITESPACE
  RESULT_VARIABLE AUDIOCPP_GIT_RESULT)
if(NOT AUDIOCPP_GIT_RESULT EQUAL 0)
  message(FATAL_ERROR "AUDIOCPP_SRC must be a Git checkout: ${AUDIOCPP_SRC}")
endif()
if(NOT AUDIOCPP_ACTUAL_COMMIT STREQUAL AUDIOCPP_PINNED_COMMIT)
  message(FATAL_ERROR
    "audio.cpp checkout mismatch. Expected ${AUDIOCPP_PINNED_COMMIT}, got ${AUDIOCPP_ACTUAL_COMMIT}. "
    "Run eng/Compare-Upstream.ps1 and review changes before updating the lock and CMake pin.")
endif()

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(ENGINE_ENABLE_CPU_ALL_VARIANTS OFF CACHE BOOL "" FORCE)
set(AUDIOCPP_DEPLOYMENT_BUILD ON CACHE BOOL "" FORCE)
set(AUDIOCPP_MODEL_SET "${AUDIOCPP_MODEL_SET}" CACHE STRING "" FORCE)
set(AUDIOCPP_MODELS "${AUDIOCPP_MODELS}" CACHE STRING "" FORCE)
set(ENGINE_ENABLE_CUDA OFF CACHE BOOL "" FORCE)
set(ENGINE_ENABLE_VULKAN OFF CACHE BOOL "" FORCE)
set(ENGINE_ENABLE_HIP OFF CACHE BOOL "" FORCE)
set(ENGINE_ENABLE_METAL OFF CACHE BOOL "" FORCE)
set(ENGINE_ENABLE_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(ENGINE_ENABLE_NATIVE_CPU OFF CACHE BOOL "" FORCE)

if(MSVC)
  # audio.cpp contains UTF-8 source text. Do not let the host ACP (for example
  # code page 936) alter identifiers or literals in a distributable build.
  add_compile_options(/utf-8)
endif()

if(AUDIOCPP_BACKEND STREQUAL "cuda")
  set(ENGINE_ENABLE_CUDA ON CACHE BOOL "" FORCE)
elseif(AUDIOCPP_BACKEND STREQUAL "vulkan")
  set(ENGINE_ENABLE_VULKAN ON CACHE BOOL "" FORCE)
elseif(AUDIOCPP_BACKEND STREQUAL "hip")
  set(ENGINE_ENABLE_HIP ON CACHE BOOL "" FORCE)
elseif(AUDIOCPP_BACKEND STREQUAL "metal")
  set(ENGINE_ENABLE_METAL ON CACHE BOOL "" FORCE)
elseif(NOT AUDIOCPP_BACKEND STREQUAL "cpu")
  message(FATAL_ERROR "Unsupported AUDIOCPP_BACKEND=${AUDIOCPP_BACKEND}")
endif()

add_subdirectory("${AUDIOCPP_SRC}" audio_cpp EXCLUDE_FROM_ALL)
add_library(audiocpp_dotnet_native SHARED native/src/audiocpp_dotnet.cpp)
target_compile_features(audiocpp_dotnet_native PRIVATE cxx_std_17)
target_compile_definitions(audiocpp_dotnet_native PRIVATE
  AUDIOCPP_DOTNET_BUILD
  AUDIOCPP_DOTNET_BACKEND="${AUDIOCPP_BACKEND}")
target_include_directories(audiocpp_dotnet_native PRIVATE native/include "${AUDIOCPP_SRC}/include")
target_link_libraries(audiocpp_dotnet_native PRIVATE engine_runtime)
set_target_properties(audiocpp_dotnet_native PROPERTIES OUTPUT_NAME audiocpp_dotnet_native)

add_executable(audiocpp_dotnet_abi_smoke native/tests/abi_smoke.cpp)
target_include_directories(audiocpp_dotnet_abi_smoke PRIVATE native/include)
target_link_libraries(audiocpp_dotnet_abi_smoke PRIVATE audiocpp_dotnet_native)

if(APPLE)
  target_link_options(audiocpp_dotnet_native PRIVATE "-Wl,-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/native/exports/exports_macos.txt")
elseif(UNIX)
  target_link_options(audiocpp_dotnet_native PRIVATE "-Wl,--exclude-libs,ALL" "-Wl,--version-script,${CMAKE_CURRENT_SOURCE_DIR}/native/exports/exports_linux.map")
elseif(WIN32)
  target_link_options(audiocpp_dotnet_native PRIVATE "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/native/exports/exports_windows.def")
endif()
