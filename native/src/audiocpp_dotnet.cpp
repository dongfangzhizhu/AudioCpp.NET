#include "audiocpp_dotnet.h"

#include "engine/framework/core/backend.h"
#include "engine/framework/core/module.h"
#include "engine/framework/runtime/registry.h"
#include "engine/framework/runtime/session.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>
#include <unordered_map>

namespace {
constexpr const char * kCommit = "78d47706c30ef215ba9ad3559baff309efeb5260";
constexpr uint64_t kTts = 1ull << 0;
#ifndef AUDIOCPP_DOTNET_BACKEND
#define AUDIOCPP_DOTNET_BACKEND "unknown"
#endif
constexpr const char * kBackend = AUDIOCPP_DOTNET_BACKEND;

void set_error(char * buffer, size_t length, const char * message) noexcept {
    if (buffer != nullptr && length != 0) {
        std::snprintf(buffer, length, "%s", message != nullptr ? message : "unknown error");
    }
}

engine::core::BackendType parse_backend(const char * value) {
    const std::string backend = value == nullptr ? "cpu" : value;
    if (backend.empty() || backend == "cpu") return engine::core::BackendType::Cpu;
    if (backend == "cuda") return engine::core::BackendType::Cuda;
    if (backend == "hip" || backend == "rocm") return engine::core::BackendType::Hip;
    if (backend == "vulkan") return engine::core::BackendType::Vulkan;
    if (backend == "metal") return engine::core::BackendType::Metal;
    if (backend == "best") return engine::core::BackendType::BestAvailable;
    throw std::invalid_argument("unsupported backend: " + backend);
}

void parse_options(const char * json, std::unordered_map<std::string, std::string> & result) {
    // The first ABI deliberately accepts only the flat scalar option form used by
    // audio.cpp's runtime. Model-specific nested configuration comes in ABI v2.
    if (json == nullptr || *json == '\0') return;
    const std::string input(json);
    size_t i = input.find('{');
    if (i == std::string::npos) throw std::invalid_argument("options_json must be a JSON object");
    while (++i < input.size()) {
        while (i < input.size() && (input[i] == ' ' || input[i] == '\n' || input[i] == '\r' || input[i] == '\t' || input[i] == ',')) ++i;
        if (i >= input.size() || input[i] == '}') break;
        if (input[i] != '"') throw std::invalid_argument("options_json keys must be strings");
        const size_t key_start = ++i;
        const size_t key_end = input.find('"', key_start);
        if (key_end == std::string::npos) throw std::invalid_argument("unterminated options_json key");
        const std::string key = input.substr(key_start, key_end - key_start);
        i = input.find(':', key_end);
        if (i == std::string::npos) throw std::invalid_argument("invalid options_json object");
        while (++i < input.size() && (input[i] == ' ' || input[i] == '\n' || input[i] == '\r' || input[i] == '\t')) {}
        std::string value;
        if (i < input.size() && input[i] == '"') {
            const size_t value_start = ++i;
            const size_t value_end = input.find('"', value_start);
            if (value_end == std::string::npos) throw std::invalid_argument("unterminated options_json value");
            value = input.substr(value_start, value_end - value_start);
            i = value_end;
        } else {
            const size_t value_start = i;
            while (i < input.size() && input[i] != ',' && input[i] != '}') ++i;
            value = input.substr(value_start, i - value_start);
        }
        result[key] = value;
    }
}
}

struct audiocpp_model {
    std::unique_ptr<engine::runtime::ILoadedVoiceModel> model;
    engine::core::BackendConfig backend;
    std::unordered_map<std::string, std::string> session_options;
};

extern "C" {
AUDIOCPP_API int32_t audiocpp_get_abi_info(audiocpp_abi_info * out_info, char * err, size_t errlen) {
    if (out_info == nullptr || out_info->struct_size < sizeof(audiocpp_abi_info)) {
        set_error(err, errlen, "out_info is null or struct_size is too small");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    out_info->abi_major = 1;
    out_info->abi_minor = 0;
    out_info->shim_version = "audiocpp-dotnet-shim 0.1.0";
    out_info->audio_cpp_commit = kCommit;
    out_info->backend = kBackend;
    out_info->capabilities = kTts;
    return AUDIOCPP_OK;
}

AUDIOCPP_API audiocpp_model * audiocpp_model_load(const char * model_path, const char * family_hint,
                                                   const char * backend, int32_t device, int32_t threads,
                                                   const char * load_options_json, char * err, size_t errlen) {
    if (model_path == nullptr || *model_path == '\0') {
        set_error(err, errlen, "model_path is required");
        return nullptr;
    }
    try {
        auto context = std::make_unique<audiocpp_model>();
        engine::runtime::ModelLoadRequest request;
        request.model_path = std::filesystem::path(model_path);
        if (family_hint != nullptr && *family_hint != '\0') request.family_hint = std::string(family_hint);
        parse_options(load_options_json, request.options);
        context->session_options = request.options;
        context->backend.type = parse_backend(backend);
        context->backend.device = device;
        context->backend.threads = threads > 0 ? threads : 1;
        auto registry = engine::runtime::make_default_registry();
        context->model = registry.load(request);
        if (!context->model) throw std::runtime_error("registry.load returned null");
        return context.release();
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what());
        return nullptr;
    } catch (...) {
        set_error(err, errlen, "unknown model load failure");
        return nullptr;
    }
}

AUDIOCPP_API int32_t audiocpp_model_synthesize(audiocpp_model * context, const char * task, const char * text,
                                               const char * voice_id, const float * ref_pcm, int32_t ref_count,
                                               int32_t ref_sample_rate, const char * options_json,
                                               float ** out_samples, int32_t * out_count,
                                               int32_t * out_sample_rate, int32_t * out_channels,
                                               char * err, size_t errlen) {
    if (context == nullptr || context->model == nullptr || text == nullptr || *text == '\0' ||
        out_samples == nullptr || out_count == nullptr || out_sample_rate == nullptr || out_channels == nullptr) {
        set_error(err, errlen, "invalid model, text, or output argument");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_samples = nullptr; *out_count = 0; *out_sample_rate = 0; *out_channels = 0;
    try {
        engine::runtime::TaskSpec spec;
        spec.mode = engine::runtime::RunMode::Offline;
        spec.task = task != nullptr && *task != '\0' ? engine::runtime::parse_voice_task_kind(task) : engine::runtime::VoiceTaskKind::Tts;
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend;
        session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * offline = dynamic_cast<engine::runtime::IOfflineVoiceTaskSession *>(session.get());
        if (offline == nullptr) throw std::runtime_error("task does not support offline execution");
        engine::runtime::TaskRequest request;
        request.text_input = engine::runtime::Transcript{std::string(text), std::string()};
        if (voice_id != nullptr && *voice_id != '\0') {
            engine::runtime::VoiceCondition voice;
            engine::runtime::VoiceReference reference;
            reference.cached_voice_id = std::string(voice_id);
            voice.speaker = std::move(reference);
            request.voice = std::move(voice);
        }
        if (ref_pcm != nullptr && ref_count > 0) {
            if (ref_sample_rate <= 0) throw std::invalid_argument("ref_sample_rate must be positive");
            if (!request.voice.has_value()) request.voice = engine::runtime::VoiceCondition{};
            if (!request.voice->speaker.has_value()) request.voice->speaker = engine::runtime::VoiceReference{};
            engine::runtime::AudioBuffer reference;
            reference.sample_rate = ref_sample_rate;
            reference.channels = 1;
            reference.samples.assign(ref_pcm, ref_pcm + ref_count);
            request.voice->speaker->audio = std::move(reference);
        }
        parse_options(options_json, request.options);
        session->prepare(engine::runtime::build_preparation_request(request));
        const auto result = offline->run(request);
        if (!result.audio_output.has_value() || result.audio_output->samples.empty()) throw std::runtime_error("model returned no audio");
        const auto & audio = *result.audio_output;
        auto * samples = static_cast<float *>(std::malloc(audio.samples.size() * sizeof(float)));
        if (samples == nullptr) throw std::bad_alloc();
        std::memcpy(samples, audio.samples.data(), audio.samples.size() * sizeof(float));
        *out_samples = samples;
        *out_count = static_cast<int32_t>(audio.samples.size());
        *out_sample_rate = audio.sample_rate;
        *out_channels = audio.channels;
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) {
        set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED;
    } catch (...) {
        set_error(err, errlen, "unknown inference failure"); return AUDIOCPP_ERR_INFERENCE_FAILED;
    }
}

AUDIOCPP_API void audiocpp_buffer_free(void * buffer) { std::free(buffer); }
AUDIOCPP_API void audiocpp_model_free(audiocpp_model * model) { delete model; }
}
