#include "audiocpp_dotnet.h"

#include "engine/framework/core/backend.h"
#include "engine/framework/core/module.h"
#include "engine/framework/runtime/registry.h"
#include "engine/framework/runtime/session.h"
#if defined(AUDIOCPP_DOTNET_HAS_MODEL_MANAGER)
#include "engine/framework/package_manager/manager.h"
#endif

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <atomic>
#include <functional>
#include <sstream>

namespace {
constexpr const char * kCommit = "78d47706c30ef215ba9ad3559baff309efeb5260";
constexpr uint64_t kTts = 1ull << 0;
constexpr uint64_t kAsr = 1ull << 1;
#ifndef AUDIOCPP_DOTNET_BACKEND
#define AUDIOCPP_DOTNET_BACKEND "unknown"
#endif
constexpr const char * kBackend = AUDIOCPP_DOTNET_BACKEND;

void set_error(char * buffer, size_t length, const char * message) noexcept {
    if (buffer != nullptr && length != 0) {
        std::snprintf(buffer, length, "%s", message != nullptr ? message : "unknown error");
    }
}

std::string json_escape(const std::string & value) {
    std::string result;
    result.reserve(value.size() + 2);
    for (const unsigned char ch : value) {
        switch (ch) {
        case '\\': result += "\\\\"; break;
        case '"': result += "\\\""; break;
        case '\n': result += "\\n"; break;
        case '\r': result += "\\r"; break;
        case '\t': result += "\\t"; break;
        default:
            if (ch < 0x20) {
                char buffer[7] = {};
                std::snprintf(buffer, sizeof(buffer), "\\u%04x", ch);
                result += buffer;
            } else result.push_back(static_cast<char>(ch));
        }
    }
    return result;
}

char * copy_string(const std::string & value) {
    auto * result = static_cast<char *>(std::malloc(value.size() + 1));
    if (result == nullptr) throw std::bad_alloc();
    std::memcpy(result, value.c_str(), value.size() + 1);
    return result;
}

std::string loader_catalog_json() {
    const auto rows = engine::runtime::make_default_registry().advertise_loaders();
    std::ostringstream output;
    output << "{\"schema_version\":1,\"loaders\":[";
    for (size_t i = 0; i < rows.size(); ++i) {
        if (i != 0) output << ',';
        const auto & row = rows[i];
        output << "{\"family\":\"" << json_escape(row.family) << "\",\"instructions_policy\":\""
               << json_escape(row.instructions_policy) << "\",\"api_endpoints\":[";
        for (size_t e = 0; e < row.api_endpoints.size(); ++e) {
            if (e != 0) output << ',';
            output << "\"" << json_escape(row.api_endpoints[e]) << "\"";
        }
        output << "],\"tasks\":[";
        for (size_t t = 0; t < row.capabilities.supported_tasks.size(); ++t) {
            if (t != 0) output << ',';
            const auto & task = row.capabilities.supported_tasks[t];
            output << "{\"task\":\"" << json_escape(engine::runtime::to_string(task.task)) << "\",\"modes\":[";
            for (size_t m = 0; m < task.modes.size(); ++m) {
                if (m != 0) output << ',';
                output << "\"" << json_escape(engine::runtime::to_string(task.modes[m])) << "\"";
            }
            output << "]}";
        }
        output << "],\"languages\":[";
        for (size_t l = 0; l < row.capabilities.languages.size(); ++l) {
            if (l != 0) output << ',';
            output << "\"" << json_escape(row.capabilities.languages[l]) << "\"";
        }
        output << "],\"supports_speaker_reference\":" << (row.capabilities.supports_speaker_reference ? "true" : "false")
               << ",\"supports_style_condition\":" << (row.capabilities.supports_style_condition ? "true" : "false")
               << ",\"supports_timestamps\":" << (row.capabilities.supports_timestamps ? "true" : "false") << "}";
    }
    output << "]}";
    return output.str();
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

// Style conditions ride in the flat option map of ABI v1. Scalar style knobs keep
// their option keys; style tags arrive as "style_tag_<name>" pairs and are moved
// into StyleCondition::tags so upstream models see a proper style condition.
void apply_style_condition(engine::runtime::TaskRequest & request) {
    engine::runtime::StyleCondition style;
    bool has_style = false;
    if (const auto it = request.options.find("style_language"); it != request.options.end()) { style.language = it->second; has_style = true; }
    if (const auto it = request.options.find("emotion"); it != request.options.end()) { style.emotion = it->second; has_style = true; }
    auto style_float = [&](const char * key, std::optional<float> & value) {
        if (const auto it = request.options.find(key); it != request.options.end()) {
            const std::string & text = it->second;
            size_t consumed = 0;
            const float parsed = std::stof(text, &consumed);
            if (consumed != text.size()) throw std::invalid_argument(std::string(key) + " must be a plain number");
            value = parsed;
            has_style = true;
        }
    };
    style_float("speaking_rate", style.speaking_rate);
    style_float("pitch_shift", style.pitch_shift);
    style_float("energy_scale", style.energy_scale);
    constexpr const char * tag_prefix = "style_tag_";
    const size_t prefix_length = std::char_traits<char>::length(tag_prefix);
    for (auto it = request.options.begin(); it != request.options.end();) {
        if (it->first.rfind(tag_prefix, 0) == 0 && it->first.size() > prefix_length) {
            style.tags[it->first.substr(prefix_length)] = it->second;
            has_style = true;
            it = request.options.erase(it);
        } else ++it;
    }
    if (has_style) {
        request.voice = request.voice.value_or(engine::runtime::VoiceCondition{});
        request.voice->style = std::move(style);
    }
}

// ---- JSON serialization shared by audiocpp_model_run_json and the streaming ABI ----
void write_audio_json(std::ostringstream & json, const engine::runtime::AudioBuffer & audio) {
    json << "{\"sample_rate\":" << audio.sample_rate << ",\"channels\":" << audio.channels << ",\"samples\":[";
    for (size_t i = 0; i < audio.samples.size(); ++i) { if (i) json << ','; json << audio.samples[i]; }
    json << "]}";
}

void write_meta_json(std::ostringstream & json, const std::unordered_map<std::string, std::string> & meta) {
    json << '{';
    size_t meta_index = 0;
    for (const auto & item : meta) { if (meta_index++) json << ','; json << "\""
        << json_escape(item.first) << "\":\"" << json_escape(item.second) << "\""; }
    json << '}';
}

const char * artifact_kind_name(engine::runtime::ArtifactKind kind) {
    using K = engine::runtime::ArtifactKind;
    switch (kind) {
    case K::SpeakerEmbedding: return "speaker_embedding";
    case K::StyleEmbedding: return "style_embedding";
    case K::PromptEmbedding: return "prompt_embedding";
    case K::AcousticTokens: return "acoustic_tokens";
    case K::Midi: return "midi";
    case K::TranscriptAlignment: return "transcript_alignment";
    case K::DiarizationState: return "diarization_state";
    case K::VadState: return "vad_state";
    default: return "custom";
    }
}

void write_artifact_json(std::ostringstream & json, const engine::runtime::VoiceArtifact & artifact) {
    static constexpr char hex[] = "0123456789abcdef";
    json << "{\"id\":\"" << json_escape(artifact.id) << "\",\"kind\":\""
         << artifact_kind_name(artifact.kind) << "\",\"payload_hex\":\"";
    for (const auto byte : artifact.payload) {
        const auto value = static_cast<unsigned char>(byte);
        json << hex[value >> 4] << hex[value & 15];
    }
    json << "\",\"meta\":";
    write_meta_json(json, artifact.meta);
    json << '}';
}

void write_transcript_json(std::ostringstream & json, const engine::runtime::Transcript & transcript) {
    json << "{\"text\":\"" << json_escape(transcript.text) << "\",\"language\":\""
         << json_escape(transcript.language) << "\"}";
}

void write_speech_segment_json(std::ostringstream & json, const engine::runtime::SpeechSegment & segment) {
    json << "{\"start_sample\":" << segment.span.start_sample << ",\"end_sample\":" << segment.span.end_sample
         << ",\"confidence\":" << segment.confidence << ",\"text\":\"" << json_escape(segment.text) << "\"}";
}

void write_speaker_turn_json(std::ostringstream & json, const engine::runtime::SpeakerTurn & turn) {
    json << "{\"start_sample\":" << turn.span.start_sample << ",\"end_sample\":" << turn.span.end_sample
         << ",\"speaker_id\":\"" << json_escape(turn.speaker_id) << "\",\"confidence\":" << turn.confidence
         << ",\"text\":\"" << json_escape(turn.text) << "\"}";
}

void write_word_timestamp_json(std::ostringstream & json, const engine::runtime::WordTimestamp & word) {
    json << "{\"start_sample\":" << word.span.start_sample << ",\"end_sample\":" << word.span.end_sample
         << ",\"word\":\"" << json_escape(word.word) << "\",\"confidence\":" << word.confidence << "}";
}

const char * voice_activity_kind_name(engine::runtime::VoiceActivityEvent::Kind kind) {
    using K = engine::runtime::VoiceActivityEvent::Kind;
    switch (kind) {
    case K::SpeechStart: return "speech_start";
    case K::SpeechEnd: return "speech_end";
    default: return "speech_segment";
    }
}

void write_voice_activity_json(std::ostringstream & json, const engine::runtime::VoiceActivityEvent & activity) {
    json << "{\"kind\":\"" << voice_activity_kind_name(activity.kind)
         << "\",\"sample\":" << activity.sample
         << ",\"probability\":" << activity.probability
         << ",\"segment\":";
    if (activity.segment.has_value()) write_speech_segment_json(json, *activity.segment);
    else json << "null";
    json << '}';
}

void write_named_audio_json(std::ostringstream & json, const engine::runtime::NamedAudioBuffer & item) {
    json << "{\"id\":\"" << json_escape(item.id) << "\",\"audio\":";
    write_audio_json(json, item.audio);
    json << ",\"meta\":";
    write_meta_json(json, item.meta);
    json << '}';
}

void write_stream_event_json(std::ostringstream & json, const engine::runtime::StreamEvent & event) {
    json << "{\"partial_text\":";
    if (event.partial_text.has_value()) write_transcript_json(json, *event.partial_text);
    else json << "null";
    json << ",\"voice_activity\":[";
    for (size_t i = 0; i < event.voice_activity.size(); ++i) { if (i) json << ','; write_voice_activity_json(json, event.voice_activity[i]); }
    json << "],\"audio_output\":";
    if (event.audio_output.has_value()) write_audio_json(json, *event.audio_output);
    else json << "null";
    json << ",\"named_audio_outputs\":[";
    for (size_t i = 0; i < event.named_audio_outputs.size(); ++i) { if (i) json << ','; write_named_audio_json(json, event.named_audio_outputs[i]); }
    json << "],\"speaker_turns\":[";
    for (size_t i = 0; i < event.speaker_turns.size(); ++i) { if (i) json << ','; write_speaker_turn_json(json, event.speaker_turns[i]); }
    json << "],\"word_timestamps\":[";
    for (size_t i = 0; i < event.word_timestamps.size(); ++i) { if (i) json << ','; write_word_timestamp_json(json, event.word_timestamps[i]); }
    json << "],\"output_artifacts\":[";
    for (size_t i = 0; i < event.output_artifacts.size(); ++i) { if (i) json << ','; write_artifact_json(json, event.output_artifacts[i]); }
    json << "],\"is_final\":" << (event.is_final ? "true" : "false") << '}';
}

// Emitted by both audiocpp_model_run_json and audiocpp_stream_finish; keep the
// schema identical so managed callers can parse both with the same types.
void write_task_result_json(std::ostringstream & json, const engine::runtime::TaskResult & result) {
    json << "{\"schema_version\":" << AUDIOCPP_STRUCTURED_RESULT_SCHEMA_VERSION;
    if (result.text_output.has_value()) json << ",\"text_output\":\"" << json_escape(result.text_output->text) << "\"";
    json << ",\"audio_output\":";
    if (result.audio_output.has_value()) write_audio_json(json, *result.audio_output);
    else json << "null";
    json << ",\"named_audio_outputs\":[";
    for (size_t i = 0; i < result.named_audio_outputs.size(); ++i) { if (i) json << ','; write_named_audio_json(json, result.named_audio_outputs[i]); }
    json << "],\"speech_segments\":[";
    for (size_t i = 0; i < result.speech_segments.size(); ++i) { if (i) json << ','; write_speech_segment_json(json, result.speech_segments[i]); }
    json << "],\"speaker_turns\":[";
    for (size_t i = 0; i < result.speaker_turns.size(); ++i) { if (i) json << ','; write_speaker_turn_json(json, result.speaker_turns[i]); }
    json << "],\"word_timestamps\":[";
    for (size_t i = 0; i < result.word_timestamps.size(); ++i) { if (i) json << ','; write_word_timestamp_json(json, result.word_timestamps[i]); }
    json << "],\"artifact_output\":";
    if (result.artifact_output.has_value()) write_artifact_json(json, *result.artifact_output);
    else json << "null";
    json << ",\"output_artifacts\":[";
    for (size_t i = 0; i < result.output_artifacts.size(); ++i) { if (i) json << ','; write_artifact_json(json, result.output_artifacts[i]); }
    json << "]}";
}
}

struct audiocpp_model {
    std::unique_ptr<engine::runtime::ILoadedVoiceModel> model;
    engine::core::BackendConfig backend;
    std::unordered_map<std::string, std::string> session_options;
};

struct audiocpp_stream {
    std::unique_ptr<engine::runtime::IVoiceTaskSession> session;
    engine::runtime::IStreamingVoiceTaskSession * streaming = nullptr;
    std::string family;
    std::string task;
    int64_t samples_pushed = 0;
    engine::runtime::StreamingInputKind input = engine::runtime::StreamingInputKind::AudioChunks;
    engine::runtime::StreamingOutputKind output = engine::runtime::StreamingOutputKind::FinalResult;
};

extern "C" {
AUDIOCPP_API int32_t audiocpp_get_abi_info(audiocpp_abi_info * out_info, char * err, size_t errlen) {
    if (out_info == nullptr || out_info->struct_size < sizeof(audiocpp_abi_info)) {
        set_error(err, errlen, "out_info is null or struct_size is too small");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    out_info->abi_major = 1;
    out_info->abi_minor = 2;
    out_info->shim_version = "audiocpp-dotnet-shim 0.3.0";
    out_info->audio_cpp_commit = kCommit;
    out_info->backend = kBackend;
    out_info->capabilities = AUDIOCPP_CAP_SYNTHESIZE |
        AUDIOCPP_CAP_TRANSCRIBE |
        AUDIOCPP_CAP_MODEL_MANAGER |
        AUDIOCPP_CAP_STRUCTURED_RESULTS |
        AUDIOCPP_CAP_STREAMING;
    return AUDIOCPP_OK;
}

AUDIOCPP_API int32_t audiocpp_get_loader_catalog(char ** out_json, char * err, size_t errlen) {
    if (out_json == nullptr) {
        set_error(err, errlen, "out_json is null");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    try {
        *out_json = copy_string(loader_catalog_json());
        return AUDIOCPP_OK;
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what());
        return AUDIOCPP_ERR_INFERENCE_FAILED;
    }
}

AUDIOCPP_API int32_t audiocpp_get_package_catalog(char ** out_json, char * err, size_t errlen) {
    if (out_json == nullptr) {
        set_error(err, errlen, "out_json is null");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
#if !defined(AUDIOCPP_DOTNET_HAS_MODEL_MANAGER)
    set_error(err, errlen, "model manager is not enabled in this native build; configure with AUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON");
    return AUDIOCPP_ERR_UNSUPPORTED;
#else
    try {
        engine::package_manager::PackageManager manager(".", ".");
        *out_json = copy_string(manager.inventory(false));
        return AUDIOCPP_OK;
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what());
        return AUDIOCPP_ERR_DOWNLOAD_FAILED;
    }
#endif
}

AUDIOCPP_API int32_t audiocpp_install_package(
    const char * package_id, const char * repository_root, const char * models_root, int32_t overwrite,
    audiocpp_download_progress_callback progress, void * progress_user_data,
    char ** out_message, char * err, size_t errlen) {
    if (package_id == nullptr || *package_id == '\0' || out_message == nullptr) {
        set_error(err, errlen, "package_id and out_message are required");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_message = nullptr;
#if !defined(AUDIOCPP_DOTNET_HAS_MODEL_MANAGER)
    set_error(err, errlen, "model manager is not enabled in this native build; configure with AUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON");
    return AUDIOCPP_ERR_UNSUPPORTED;
#else
    try {
        engine::package_manager::PackageManager manager(
            repository_root != nullptr && *repository_root != '\0' ? repository_root : ".",
            models_root != nullptr && *models_root != '\0' ? models_root : "models");
        auto cancelled = std::make_shared<std::atomic_bool>(false);
        const auto message = manager.install(package_id, overwrite != 0, cancelled,
            [progress, progress_user_data](const engine::package_manager::PackageProgress & item) {
                if (progress != nullptr) progress(item.downloaded_bytes, item.total_bytes, item.message.c_str(), progress_user_data);
            });
        *out_message = copy_string(message);
        return AUDIOCPP_OK;
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what());
        return AUDIOCPP_ERR_DOWNLOAD_FAILED;
    }
#endif
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
        apply_style_condition(request);
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

AUDIOCPP_API int32_t audiocpp_model_transcribe(
    audiocpp_model * context, const float * audio_samples, int32_t audio_count,
    int32_t audio_sample_rate, int32_t audio_channels, const char * options_json,
    char ** out_text, char * err, size_t errlen) {
    if (context == nullptr || context->model == nullptr || audio_samples == nullptr || audio_count <= 0 ||
        audio_sample_rate <= 0 || audio_channels <= 0 || out_text == nullptr) {
        set_error(err, errlen, "invalid model, audio, or output argument");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_text = nullptr;
    try {
        engine::runtime::TaskSpec spec;
        spec.task = engine::runtime::VoiceTaskKind::Asr;
        spec.mode = engine::runtime::RunMode::Offline;
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend;
        session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * offline = dynamic_cast<engine::runtime::IOfflineVoiceTaskSession *>(session.get());
        if (offline == nullptr) throw std::runtime_error("task does not support offline execution");
        engine::runtime::TaskRequest request;
        request.audio_input = engine::runtime::AudioBuffer{};
        request.audio_input->sample_rate = audio_sample_rate;
        request.audio_input->channels = audio_channels;
        request.audio_input->samples.assign(audio_samples, audio_samples + audio_count);
        parse_options(options_json, request.options);
        session->prepare(engine::runtime::build_preparation_request(request));
        const auto result = offline->run(request);
        if (!result.text_output.has_value()) throw std::runtime_error("model returned no transcription");
        *out_text = copy_string(result.text_output->text);
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) {
        set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) {
        set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED;
    } catch (...) {
        set_error(err, errlen, "unknown transcription failure"); return AUDIOCPP_ERR_INFERENCE_FAILED;
    }
}

AUDIOCPP_API int32_t audiocpp_model_run_json(
    audiocpp_model * context, const char * task, const char * text,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * options_json,
    char ** out_json, char * err, size_t errlen) {
    if (context == nullptr || context->model == nullptr || out_json == nullptr) {
        set_error(err, errlen, "invalid model or output argument"); return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    try {
        engine::runtime::TaskSpec spec;
        // Default the task family from the request shape: audio-only input is
        // transcription, otherwise generation. An explicit task string wins.
        const bool has_audio_input = audio_samples != nullptr && audio_count > 0;
        spec.task = engine::runtime::parse_voice_task_kind(
            task == nullptr || *task == '\0' ? (has_audio_input ? "asr" : "tts") : task);
        spec.mode = engine::runtime::RunMode::Offline;
        engine::runtime::TaskRequest request;
        if (text != nullptr && *text != '\0') request.text_input = engine::runtime::Transcript{std::string(text), ""};
        if (audio_samples != nullptr && audio_count > 0) {
            request.audio_input = engine::runtime::AudioBuffer{};
            request.audio_input->sample_rate = audio_sample_rate;
            request.audio_input->channels = audio_channels;
            request.audio_input->samples.assign(audio_samples, audio_samples + audio_count);
        }
        if (voice_id != nullptr && *voice_id != '\0') {
            request.voice = engine::runtime::VoiceCondition{};
            request.voice->speaker = engine::runtime::VoiceReference{};
            request.voice->speaker->cached_voice_id = std::string(voice_id);
        }
        if (ref_pcm != nullptr && ref_count > 0) {
            request.voice = request.voice.value_or(engine::runtime::VoiceCondition{});
            request.voice->speaker = request.voice->speaker.value_or(engine::runtime::VoiceReference{});
            engine::runtime::AudioBuffer reference;
            reference.sample_rate = ref_sample_rate; reference.channels = 1;
            reference.samples.assign(ref_pcm, ref_pcm + ref_count);
            request.voice->speaker->audio = std::move(reference);
        }
        parse_options(options_json, request.options);
        apply_style_condition(request);
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend; session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * offline = dynamic_cast<engine::runtime::IOfflineVoiceTaskSession *>(session.get());
        if (offline == nullptr) throw std::runtime_error("task does not support offline execution");
        session->prepare(engine::runtime::build_preparation_request(request));
        const auto result = offline->run(request);
        std::ostringstream json;
        write_task_result_json(json, result);
        *out_json = copy_string(json.str()); return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}

AUDIOCPP_API int32_t audiocpp_stream_open(
    audiocpp_model * context, const char * task, const char * options_json,
    audiocpp_stream ** out_stream, char ** out_info, char * err, size_t errlen) {
    if (context == nullptr || context->model == nullptr || out_stream == nullptr || out_info == nullptr) {
        set_error(err, errlen, "invalid model or output argument"); return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_stream = nullptr;
    *out_info = nullptr;
    if (task == nullptr || *task == '\0') {
        set_error(err, errlen, "streaming task is required (e.g. \"asr\", \"vad\")"); return AUDIOCPP_ERR_BAD_ARG;
    }
    try {
        const auto task_kind = engine::runtime::parse_voice_task_kind(task);
        bool streaming_supported = false;
        for (const auto & supported : context->model->capabilities().supported_tasks) {
            if (supported.task != task_kind) continue;
            for (const auto mode : supported.modes) {
                if (mode == engine::runtime::RunMode::Streaming) streaming_supported = true;
            }
        }
        if (!streaming_supported) {
            set_error(err, errlen, "model does not support streaming for the requested task");
            return AUDIOCPP_ERR_UNSUPPORTED;
        }
        engine::runtime::TaskSpec spec;
        spec.task = task_kind;
        spec.mode = engine::runtime::RunMode::Streaming;
        engine::runtime::TaskRequest request;
        parse_options(options_json, request.options);
        apply_style_condition(request);
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend; session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * streaming = dynamic_cast<engine::runtime::IStreamingVoiceTaskSession *>(session.get());
        if (streaming == nullptr) {
            set_error(err, errlen, "model does not provide a streaming session for the requested task");
            return AUDIOCPP_ERR_UNSUPPORTED;
        }
        session->prepare(engine::runtime::build_preparation_request(request));
        streaming->start_stream(request);
        const auto policy = streaming->streaming_policy();
        std::ostringstream info;
        info << "{\"family\":\"" << json_escape(session->family()) << "\",\"task\":\""
             << json_escape(engine::runtime::to_string(task_kind))
             << "\",\"input\":\"" << (policy.input == engine::runtime::StreamingInputKind::AudioChunks ? "audio_chunks" : "none")
             << "\",\"output\":\"" << (policy.output == engine::runtime::StreamingOutputKind::PullEvents ? "pull_events" : "final_result")
             << "\",\"preferred_chunk_samples\":" << policy.preferred_audio_chunk_samples
             << ",\"preferred_chunk_seconds\":" << policy.preferred_audio_chunk_seconds << "}";
        auto handle = std::make_unique<audiocpp_stream>();
        handle->session = std::move(session);
        handle->streaming = streaming;
        handle->family = handle->session->family();
        handle->task = task;
        handle->input = policy.input;
        handle->output = policy.output;
        *out_info = copy_string(info.str());
        *out_stream = handle.release();
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED; }
    catch (...) { set_error(err, errlen, "unknown streaming open failure"); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}

AUDIOCPP_API int32_t audiocpp_stream_push_pcm(
    audiocpp_stream * stream, const float * samples, int32_t sample_count,
    int32_t sample_rate, int32_t channels,
    char ** out_json, char * err, size_t errlen) {
    if (stream == nullptr || stream->streaming == nullptr || out_json == nullptr) {
        set_error(err, errlen, "invalid stream or output argument"); return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    if (samples == nullptr || sample_count <= 0) {
        set_error(err, errlen, "samples must point to at least one float"); return AUDIOCPP_ERR_BAD_ARG;
    }
    if (sample_rate <= 0 || channels <= 0) {
        set_error(err, errlen, "sample_rate and channels must be positive"); return AUDIOCPP_ERR_BAD_ARG;
    }
    try {
        engine::runtime::AudioChunk chunk;
        chunk.sample_rate = sample_rate;
        chunk.channels = channels;
        chunk.start_sample = stream->samples_pushed;
        chunk.samples.assign(samples, samples + sample_count);
        stream->samples_pushed += sample_count;
        std::ostringstream json;
        json << "{\"events\":[";
        bool first = true;
        const auto emit = [&](const engine::runtime::StreamEvent & event) {
            if (!first) json << ',';
            first = false;
            write_stream_event_json(json, event);
        };
        emit(stream->streaming->process_audio_chunk(chunk));
        if (stream->output == engine::runtime::StreamingOutputKind::PullEvents) {
            while (auto event = stream->streaming->next_stream_event()) emit(*event);
        }
        json << "]}";
        *out_json = copy_string(json.str());
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED; }
    catch (...) { set_error(err, errlen, "unknown streaming push failure"); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}

AUDIOCPP_API int32_t audiocpp_stream_finish(audiocpp_stream * stream, char ** out_json, char * err, size_t errlen) {
    if (stream == nullptr || stream->streaming == nullptr || out_json == nullptr) {
        set_error(err, errlen, "invalid stream or output argument"); return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    try {
        const auto result = stream->streaming->finish_stream();
        std::ostringstream json;
        write_task_result_json(json, result);
        *out_json = copy_string(json.str());
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED; }
    catch (...) { set_error(err, errlen, "unknown streaming finish failure"); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}

AUDIOCPP_API void audiocpp_stream_free(audiocpp_stream * stream) { delete stream; }

AUDIOCPP_API void audiocpp_buffer_free(void * buffer) { std::free(buffer); }
AUDIOCPP_API void audiocpp_model_free(audiocpp_model * model) { delete model; }
}
