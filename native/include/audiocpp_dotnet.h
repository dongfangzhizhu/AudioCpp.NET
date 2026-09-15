#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(AUDIOCPP_DOTNET_BUILD)
#    define AUDIOCPP_API __declspec(dllexport)
#  else
#    define AUDIOCPP_API __declspec(dllimport)
#  endif
#else
#  define AUDIOCPP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum audiocpp_status {
    AUDIOCPP_OK = 0,
    AUDIOCPP_ERR_BAD_ARG = 1,
    AUDIOCPP_ERR_LOAD_FAILED = 2,
    AUDIOCPP_ERR_INFERENCE_FAILED = 3,
    AUDIOCPP_ERR_UNSUPPORTED = 4,
    AUDIOCPP_ERR_DOWNLOAD_FAILED = 5,
} audiocpp_status;

enum {
    AUDIOCPP_CAP_SYNTHESIZE = 1ull << 0,
    AUDIOCPP_CAP_TRANSCRIBE = 1ull << 1,
    AUDIOCPP_CAP_MODEL_MANAGER = 1ull << 2,
    AUDIOCPP_CAP_STRUCTURED_RESULTS = 1ull << 3,
    AUDIOCPP_CAP_STREAMING = 1ull << 4,
};

#define AUDIOCPP_STRUCTURED_RESULT_SCHEMA_VERSION 1u

typedef struct audiocpp_abi_info {
    uint32_t struct_size;
    uint32_t abi_major;
    uint32_t abi_minor;
    const char * shim_version;
    const char * audio_cpp_commit;
    const char * backend;
    uint64_t capabilities;
} audiocpp_abi_info;

typedef struct audiocpp_model audiocpp_model;
typedef void (*audiocpp_download_progress_callback)(
    uint64_t downloaded_bytes, uint64_t total_bytes, const char * message, void * user_data);

AUDIOCPP_API int32_t audiocpp_get_abi_info(audiocpp_abi_info * out_info, char * err, size_t errlen);
AUDIOCPP_API audiocpp_model * audiocpp_model_load(
    const char * model_path,
    const char * family_hint,
    const char * backend,
    int32_t device,
    int32_t threads,
    const char * load_options_json,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_model_synthesize(
    audiocpp_model * model,
    const char * task,
    const char * text,
    const char * voice_id,
    const float * ref_pcm,
    int32_t ref_count,
    int32_t ref_sample_rate,
    const char * options_json,
    float ** out_samples,
    int32_t * out_count,
    int32_t * out_sample_rate,
    int32_t * out_channels,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_model_transcribe(
    audiocpp_model * model,
    const float * audio_samples,
    int32_t audio_count,
    int32_t audio_sample_rate,
    int32_t audio_channels,
    const char * options_json,
    char ** out_text,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_get_loader_catalog(char ** out_json, char * err, size_t errlen);
AUDIOCPP_API int32_t audiocpp_model_run_json(
    audiocpp_model * model, const char * task, const char * text,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * options_json,
    char ** out_json, char * err, size_t errlen);
AUDIOCPP_API int32_t audiocpp_get_package_catalog(char ** out_json, char * err, size_t errlen);
AUDIOCPP_API int32_t audiocpp_install_package(
    const char * package_id,
    const char * repository_root,
    const char * models_root,
    int32_t overwrite,
    audiocpp_download_progress_callback progress,
    void * progress_user_data,
    char ** out_message,
    char * err,
    size_t errlen);
AUDIOCPP_API void audiocpp_buffer_free(void * buffer);
AUDIOCPP_API void audiocpp_model_free(audiocpp_model * model);

#ifdef __cplusplus
}
#endif
