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

/*
 * Capability bits reported in audiocpp_abi_info::capabilities. The definitions
 * live here (and are mirrored by AudioCppCapabilities in managed code) so no
 * caller has to hard-code a magic number.
 */
enum {
    AUDIOCPP_CAP_SYNTHESIZE = 1ull << 0,
    AUDIOCPP_CAP_TRANSCRIBE = 1ull << 1,
    AUDIOCPP_CAP_MODEL_MANAGER = 1ull << 2,
    AUDIOCPP_CAP_STRUCTURED_RESULTS = 1ull << 3,
    AUDIOCPP_CAP_STREAMING = 1ull << 4,
    /* audiocpp_get_task_catalog is available and the task parser accepts both
     * canonical tokens and the model_spec aliases. */
    AUDIOCPP_CAP_TASK_CATALOG = 1ull << 5,
    /* VoiceArtifact payloads can be supplied on input and are returned on output. */
    AUDIOCPP_CAP_ARTIFACTS = 1ull << 6,
    /* audiocpp_model_run_json_ex / audiocpp_stream_open_ex accept text_language
     * and artifacts_json. */
    AUDIOCPP_CAP_EXEC_OPTIONS = 1ull << 7,
};

/*
 * Version of the JSON emitted by audiocpp_model_run_json(_ex) and
 * audiocpp_stream_finish. Both entry points deliberately share one schema so a
 * managed caller parses them with a single reader.
 *
 * 2 - added "task": the canonical token of the task that actually ran. Callers
 *     that omit the task argument (the shim infers it from the request shape)
 *     can now read back what was inferred.
 */
#define AUDIOCPP_STRUCTURED_RESULT_SCHEMA_VERSION 2u
#define AUDIOCPP_TASK_CATALOG_SCHEMA_VERSION 1u

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
typedef struct audiocpp_stream audiocpp_stream;
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
/*
 * Enumerates every task the shim can dispatch, in canonical form, together with
 * the model_spec aliases that resolve to it. Required capabilities per task are
 * reported so callers can grey out unsupported entries instead of failing.
 */
AUDIOCPP_API int32_t audiocpp_get_task_catalog(char ** out_json, char * err, size_t errlen);
/*
 * Structured execution. `task` accepts a canonical token or a model_spec alias.
 * When `task` is null/empty the family is inferred from the request shape:
 * audio-only input becomes asr, otherwise tts.
 *
 * `audio_samples` is interleaved; when `audio_channels` > 1 the shim down-mixes
 * to mono before handing the buffer to the model.
 *
 * `artifacts_json` is an array of
 *   {"kind":"speaker_embedding","id":"…","payload_hex":"…","payload_text":"…","meta":{…}}
 * describing TaskRequest::input_artifacts. Unknown kinds are rejected.
 */
AUDIOCPP_API int32_t audiocpp_model_run_json(
    audiocpp_model * model, const char * task, const char * text,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * options_json,
    char ** out_json, char * err, size_t errlen);
AUDIOCPP_API int32_t audiocpp_model_run_json_ex(
    audiocpp_model * model, const char * task, const char * text, const char * text_language,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * artifacts_json,
    const char * options_json, char ** out_json, char * err, size_t errlen);
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
AUDIOCPP_API int32_t audiocpp_stream_open(
    audiocpp_model * model,
    const char * task,
    const char * options_json,
    audiocpp_stream ** out_stream,
    char ** out_info,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_stream_open_ex(
    audiocpp_model * model,
    const char * task,
    const char * text,
    const char * text_language,
    const char * artifacts_json,
    const char * options_json,
    audiocpp_stream ** out_stream,
    char ** out_info,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_stream_push_pcm(
    audiocpp_stream * stream,
    const float * samples,
    int32_t sample_count,
    int32_t sample_rate,
    int32_t channels,
    char ** out_json,
    char * err,
    size_t errlen);
AUDIOCPP_API int32_t audiocpp_stream_finish(
    audiocpp_stream * stream,
    char ** out_json,
    char * err,
    size_t errlen);
AUDIOCPP_API void audiocpp_stream_free(audiocpp_stream * stream);
AUDIOCPP_API void audiocpp_buffer_free(void * buffer);
AUDIOCPP_API void audiocpp_model_free(audiocpp_model * model);

#ifdef __cplusplus
}
#endif
