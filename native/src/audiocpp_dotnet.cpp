#include "audiocpp_dotnet.h"

#include "engine/framework/core/backend.h"
#include "engine/framework/core/module.h"
#include "engine/framework/io/json.h"
#include "engine/framework/runtime/registry.h"
#include "engine/framework/runtime/session.h"
#if defined(AUDIOCPP_DOTNET_HAS_MODEL_MANAGER)
#include "engine/framework/package_manager/manager.h"
#endif

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <atomic>
#include <functional>
#include <sstream>
#include <vector>

namespace {
constexpr const char * kCommit = "78d47706c30ef215ba9ad3559baff309efeb5260";
#ifdef AUDIOCPP_DOTNET_BACKEND
constexpr const char * kBackend = AUDIOCPP_DOTNET_BACKEND;
#else
constexpr const char * kBackend = "unknown";
#endif

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

// ---- task tokens -----------------------------------------------------------------
//
// audiocpp_model_run_json/stream_open accept the canonical tokens used by
// runtime::to_string(VoiceTaskKind) *and* the tokens that appear in
// upstream's model_specs/*.json "tasks" arrays. Upstream keeps that second table
// private to src/framework/model_spec/metadata.cpp, so the mapping is mirrored
// here; audiocpp_get_task_catalog publishes it back to callers.
struct TaskAlias {
    const char * token;
    engine::runtime::VoiceTaskKind kind;
};

constexpr TaskAlias kTaskAliases[] = {
    {"audio_generation", engine::runtime::VoiceTaskKind::AudioGeneration},
    {"music", engine::runtime::VoiceTaskKind::AudioGeneration},
    {"sfx", engine::runtime::VoiceTaskKind::AudioGeneration},
    {"edit", engine::runtime::VoiceTaskKind::AudioGeneration},
    {"clone", engine::runtime::VoiceTaskKind::VoiceCloning},
    {"design", engine::runtime::VoiceTaskKind::VoiceDesign},
    {"speaker", engine::runtime::VoiceTaskKind::SpeakerRecognition},
    {"codec", engine::runtime::VoiceTaskKind::VoiceConversion},
};

struct TaskDescriptor {
    engine::runtime::VoiceTaskKind kind;
    const char * token;
    const char * input;            // audio | text | audio+text
    const char * typical_outputs;  // comma-separated TaskResult channels
};

constexpr TaskDescriptor kTaskCatalog[] = {
    {engine::runtime::VoiceTaskKind::Vad, "vad", "audio", "speech_segments"},
    {engine::runtime::VoiceTaskKind::Asr, "asr", "audio", "text_output,word_timestamps,speech_segments"},
    {engine::runtime::VoiceTaskKind::Diarization, "diar", "audio", "speaker_turns"},
    {engine::runtime::VoiceTaskKind::SourceSeparation, "sep", "audio", "named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::AudioGeneration, "gen", "text", "audio_output,named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::Tts, "tts", "text", "audio_output,named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::VoiceCloning, "clon", "text", "audio_output,named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::VoiceConversion, "vc", "audio+text", "audio_output,named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::SpeechToSpeech, "s2s", "audio+text", "audio_output,named_audio_outputs"},
    {engine::runtime::VoiceTaskKind::Alignment, "align", "audio+text", "word_timestamps"},
    {engine::runtime::VoiceTaskKind::VoiceDesign, "vdes", "text", "audio_output"},
    {engine::runtime::VoiceTaskKind::SpeakerRecognition, "spk", "audio", "artifact_output"},
    {engine::runtime::VoiceTaskKind::Svc, "svc", "audio+text", "audio_output"},
    {engine::runtime::VoiceTaskKind::Midi, "midi", "audio", "artifact_output"},
};

std::string lowercase(std::string value) {
    for (char & ch : value) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    return value;
}

// Throws std::invalid_argument with the full accepted-token list so callers get
// an actionable message instead of a bare "unsupported task".
engine::runtime::VoiceTaskKind parse_task_kind(const char * value) {
    std::string token = lowercase(value == nullptr ? std::string() : std::string(value));
    if (token.empty()) throw std::invalid_argument("task must not be empty");
    for (const auto & entry : kTaskCatalog) {
        if (token == entry.token) return entry.kind;
    }
    for (const auto & entry : kTaskAliases) {
        if (token != entry.token) continue;
        // "codec" is advertised by miocodec's spec but upstream has no codec
        // kind; the model's own vc/s2s sessions are the supported path.
        return entry.kind;
    }
    std::string accepted;
    for (const auto & entry : kTaskCatalog) { if (!accepted.empty()) accepted += ", "; accepted += entry.token; }
    for (const auto & entry : kTaskAliases) { accepted += ", "; accepted += entry.token; }
    throw std::invalid_argument("unsupported task: " + token + " (expected one of " + accepted + ")");
}

std::string task_catalog_json() {
    std::ostringstream output;
    output << "{\"schema_version\":" << AUDIOCPP_TASK_CATALOG_SCHEMA_VERSION << ",\"tasks\":[";
    for (size_t i = 0; i < sizeof(kTaskCatalog) / sizeof(kTaskCatalog[0]); ++i) {
        if (i != 0) output << ',';
        const auto & entry = kTaskCatalog[i];
        output << "{\"task\":\"" << json_escape(entry.token) << "\",\"input\":\"" << entry.input
               << "\",\"typical_outputs\":[\"";
        const std::string channels(entry.typical_outputs);
        size_t start = 0;
        bool first_channel = true;
        while (start <= channels.size()) {
            const size_t comma = channels.find(',', start);
            const std::string channel = channels.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
            if (!first_channel) output << "\",\"";
            first_channel = false;
            output << channel;
            if (comma == std::string::npos) break;
            start = comma + 1;
        }
        output << "\"],\"aliases\":[";
        bool first_alias = true;
        for (const auto & alias : kTaskAliases) {
            if (alias.kind != entry.kind) continue;
            if (!first_alias) output << ',';
            first_alias = false;
            output << "\"" << alias.token << "\"";
        }
        output << "]}";
    }
    output << "]}";
    return output.str();
}

// ---- loader catalog --------------------------------------------------------------
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
    // Options stay a flat scalar map: every runtime option upstream defines is a
    // scalar, and nested model configuration travels through load_options_json.
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

// Style conditions ride in the flat option map. Scalar style knobs keep their
// option keys; style tags arrive as "style_tag_<name>" pairs and are moved into
// StyleCondition::tags so upstream models see a proper style condition.
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

// ---- artifact input --------------------------------------------------------------
engine::runtime::ArtifactKind artifact_kind_from_name(const std::string & name) {
    using K = engine::runtime::ArtifactKind;
    if (name == "speaker_embedding") return K::SpeakerEmbedding;
    if (name == "style_embedding") return K::StyleEmbedding;
    if (name == "prompt_embedding") return K::PromptEmbedding;
    if (name == "acoustic_tokens") return K::AcousticTokens;
    if (name == "midi") return K::Midi;
    if (name == "transcript_alignment") return K::TranscriptAlignment;
    if (name == "diarization_state") return K::DiarizationState;
    if (name == "vad_state") return K::VadState;
    if (name == "custom" || name.empty()) return K::Custom;
    throw std::invalid_argument(
        "unsupported artifact kind: " + name +
        " (expected speaker_embedding, style_embedding, prompt_embedding, acoustic_tokens, midi, "
        "transcript_alignment, diarization_state, vad_state, or custom)");
}

int hex_digit(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

std::vector<std::byte> decode_hex(const std::string & text) {
    if (text.size() % 2 != 0) throw std::invalid_argument("artifact payload_hex must have an even number of digits");
    std::vector<std::byte> payload;
    payload.reserve(text.size() / 2);
    for (size_t i = 0; i < text.size(); i += 2) {
        const int high = hex_digit(text[i]);
        const int low = hex_digit(text[i + 1]);
        if (high < 0 || low < 0) throw std::invalid_argument("artifact payload_hex contains a non-hexadecimal digit");
        payload.push_back(static_cast<std::byte>((high << 4) | low));
    }
    return payload;
}

std::vector<engine::runtime::VoiceArtifact> parse_artifacts(const char * json) {
    std::vector<engine::runtime::VoiceArtifact> artifacts;
    if (json == nullptr || *json == '\0') return artifacts;
    const auto root = engine::io::json::parse(json);
    if (!root.is_array()) throw std::invalid_argument("artifacts_json must be a JSON array");
    for (const auto & item : root.as_array()) {
        if (!item.is_object()) throw std::invalid_argument("artifacts_json entries must be JSON objects");
        const std::string kind_name = engine::io::json::optional_string(item, "kind", "");
        engine::runtime::VoiceArtifact artifact;
        artifact.kind = artifact_kind_from_name(kind_name);
        artifact.id = engine::io::json::optional_string(item, "id", "");
        if (const auto * payload_hex = item.find("payload_hex");
            payload_hex != nullptr && payload_hex->is_string() && !payload_hex->as_string().empty()) {
            artifact.payload = decode_hex(payload_hex->as_string());
        } else if (const auto * payload_text = item.find("payload_text");
                   payload_text != nullptr && payload_text->is_string()) {
            artifact.payload = engine::runtime::bytes_from_string(payload_text->as_string());
        }
        if (const auto * meta = item.find("meta"); meta != nullptr && meta->is_object()) {
            for (const auto & [key, value] : meta->as_object()) {
                artifact.meta[key] = value.is_string() ? value.as_string()
                    : value.is_number() ? engine::io::json::stringify_number(value.as_number())
                    : value.is_bool() ? std::string(value.as_bool() ? "true" : "false") : std::string();
            }
        }
        artifacts.push_back(std::move(artifact));
    }
    return artifacts;
}

// ---- audio input -----------------------------------------------------------------
// The ABI takes interleaved PCM. When the caller passes more than one channel the
// shim down-mixes to mono before handing samples to the model, so every loader
// sees the same well-defined layout instead of having to guess.
engine::runtime::AudioBuffer make_audio_buffer(
    const float * samples, int32_t count, int32_t sample_rate, int32_t channels) {
    if (samples == nullptr || count <= 0) throw std::invalid_argument("audio samples must not be empty");
    if (sample_rate <= 0) throw std::invalid_argument("audio_sample_rate must be positive");
    if (channels <= 0) throw std::invalid_argument("audio_channels must be positive");
    engine::runtime::AudioBuffer audio;
    audio.sample_rate = sample_rate;
    if (channels == 1) {
        audio.channels = 1;
        audio.samples.assign(samples, samples + count);
        return audio;
    }
    const int32_t frames = count / channels;
    if (frames <= 0) throw std::invalid_argument("audio sample count is smaller than the channel count");
    audio.channels = 1;
    audio.samples.resize(static_cast<size_t>(frames));
    const float scale = 1.0f / static_cast<float>(channels);
    for (int32_t frame = 0; frame < frames; ++frame) {
        float sum = 0.0f;
        for (int32_t channel = 0; channel < channels; ++channel) sum += samples[static_cast<size_t>(frame) * channels + channel];
        audio.samples[static_cast<size_t>(frame)] = sum * scale;
    }
    return audio;
}

// ---- JSON serialization shared by run_json and the streaming ABI ------------------
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

// Emitted by both audiocpp_model_run_json_ex and audiocpp_stream_finish; keep the
// schema identical so managed callers can parse both with the same types.
// `task` is the canonical token that actually ran, so callers that let the shim
// infer the family from the request shape can read back what was chosen.
void write_task_result_json(std::ostringstream & json, const engine::runtime::TaskResult & result, const char * task) {
    json << "{\"schema_version\":" << AUDIOCPP_STRUCTURED_RESULT_SCHEMA_VERSION;
    if (task != nullptr && *task != '\0') json << ",\"task\":\"" << json_escape(task) << "\"";
    if (result.text_output.has_value()) {
        json << ",\"text_output\":\"" << json_escape(result.text_output->text) << "\"";
        if (!result.text_output->language.empty())
            json << ",\"text_language\":\"" << json_escape(result.text_output->language) << "\"";
    }
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

// Fills everything except the task family, which the callers resolve differently.
void fill_request(
    engine::runtime::TaskRequest & request,
    const char * text, const char * text_language,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate, int32_t audio_channels,
    const char * voice_id, const float * ref_pcm, int32_t ref_count, int32_t ref_sample_rate,
    const char * artifacts_json, const char * options_json) {
    if (text != nullptr && *text != '\0') {
        request.text_input = engine::runtime::Transcript{
            std::string(text), text_language == nullptr ? std::string() : std::string(text_language)};
    }
    if (audio_samples != nullptr && audio_count > 0) {
        request.audio_input = make_audio_buffer(audio_samples, audio_count, audio_sample_rate, audio_channels);
    }
    if (voice_id != nullptr && *voice_id != '\0') {
        request.voice = request.voice.value_or(engine::runtime::VoiceCondition{});
        request.voice->speaker = request.voice->speaker.value_or(engine::runtime::VoiceReference{});
        request.voice->speaker->cached_voice_id = std::string(voice_id);
    }
    if (ref_pcm != nullptr && ref_count > 0) {
        if (ref_sample_rate <= 0) throw std::invalid_argument("ref_sample_rate must be positive");
        request.voice = request.voice.value_or(engine::runtime::VoiceCondition{});
        request.voice->speaker = request.voice->speaker.value_or(engine::runtime::VoiceReference{});
        engine::runtime::AudioBuffer reference;
        reference.sample_rate = ref_sample_rate;
        reference.channels = 1;
        reference.samples.assign(ref_pcm, ref_pcm + ref_count);
        request.voice->speaker->audio = std::move(reference);
    }
    request.input_artifacts = parse_artifacts(artifacts_json);
    parse_options(options_json, request.options);
    apply_style_condition(request);
}

// ---- streaming ----------------------------------------------------------------
int32_t open_stream_impl(
    audiocpp_model * context, const char * task, const char * text, const char * text_language,
    const char * artifacts_json, const char * options_json,
    audiocpp_stream ** out_stream, char ** out_info, char * err, size_t errlen);
}  // namespace

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

namespace {
int32_t open_stream_impl(
    audiocpp_model * context, const char * task, const char * text, const char * text_language,
    const char * artifacts_json, const char * options_json,
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
        const auto task_kind = parse_task_kind(task);
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
        fill_request(request, text, text_language, nullptr, 0, 0, 0, nullptr, nullptr, 0, 0, artifacts_json, options_json);
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
        handle->task = engine::runtime::to_string(task_kind);
        handle->input = policy.input;
        handle->output = policy.output;
        *out_info = copy_string(info.str());
        *out_stream = handle.release();
        return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED; }
    catch (...) { set_error(err, errlen, "unknown streaming open failure"); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}
}  // namespace

extern "C" {
AUDIOCPP_API int32_t audiocpp_get_abi_info(audiocpp_abi_info * out_info, char * err, size_t errlen) {
    if (out_info == nullptr || out_info->struct_size < sizeof(audiocpp_abi_info)) {
        set_error(err, errlen, "out_info is null or struct_size is too small");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    out_info->abi_major = 1;
    out_info->abi_minor = 3;
    out_info->shim_version = "audiocpp-dotnet-shim 0.4.0";
    out_info->audio_cpp_commit = kCommit;
    out_info->backend = kBackend;
    uint64_t capabilities = AUDIOCPP_CAP_SYNTHESIZE |
        AUDIOCPP_CAP_TRANSCRIBE |
        AUDIOCPP_CAP_STRUCTURED_RESULTS |
        AUDIOCPP_CAP_STREAMING |
        AUDIOCPP_CAP_TASK_CATALOG |
        AUDIOCPP_CAP_ARTIFACTS |
        AUDIOCPP_CAP_EXEC_OPTIONS;
    // Only advertise the package manager when this build actually links it:
    // audiocpp_get_package_catalog/audiocpp_install_package return UNSUPPORTED otherwise.
#if defined(AUDIOCPP_DOTNET_HAS_MODEL_MANAGER)
    capabilities |= AUDIOCPP_CAP_MODEL_MANAGER;
#endif
    out_info->capabilities = capabilities;
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

AUDIOCPP_API int32_t audiocpp_get_task_catalog(char ** out_json, char * err, size_t errlen) {
    if (out_json == nullptr) {
        set_error(err, errlen, "out_json is null");
        return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    try {
        *out_json = copy_string(task_catalog_json());
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
        spec.task = task != nullptr && *task != '\0' ? parse_task_kind(task) : engine::runtime::VoiceTaskKind::Tts;
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend;
        session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * offline = dynamic_cast<engine::runtime::IOfflineVoiceTaskSession *>(session.get());
        if (offline == nullptr) throw std::runtime_error("task does not support offline execution");
        engine::runtime::TaskRequest request;
        fill_request(request, text, nullptr, nullptr, 0, 0, 0, voice_id, ref_pcm, ref_count, ref_sample_rate, nullptr, options_json);
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
        fill_request(request, nullptr, nullptr, audio_samples, audio_count, audio_sample_rate, audio_channels,
                     nullptr, nullptr, 0, 0, nullptr, options_json);
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

AUDIOCPP_API int32_t audiocpp_model_run_json_ex(
    audiocpp_model * context, const char * task, const char * text, const char * text_language,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * artifacts_json,
    const char * options_json, char ** out_json, char * err, size_t errlen) {
    if (context == nullptr || context->model == nullptr || out_json == nullptr) {
        set_error(err, errlen, "invalid model or output argument"); return AUDIOCPP_ERR_BAD_ARG;
    }
    *out_json = nullptr;
    try {
        engine::runtime::TaskSpec spec;
        // Default the task family from the request shape: audio-only input is
        // transcription, otherwise generation. An explicit task string wins.
        const bool has_audio_input = audio_samples != nullptr && audio_count > 0;
        spec.task = parse_task_kind(task == nullptr || *task == '\0' ? (has_audio_input ? "asr" : "tts") : task);
        spec.mode = engine::runtime::RunMode::Offline;
        engine::runtime::TaskRequest request;
        fill_request(request, text, text_language, audio_samples, audio_count, audio_sample_rate, audio_channels,
                     voice_id, ref_pcm, ref_count, ref_sample_rate, artifacts_json, options_json);
        engine::runtime::SessionOptions session_options;
        session_options.backend = context->backend; session_options.options = context->session_options;
        auto session = context->model->create_task_session(spec, session_options);
        auto * offline = dynamic_cast<engine::runtime::IOfflineVoiceTaskSession *>(session.get());
        if (offline == nullptr) throw std::runtime_error("task does not support offline execution");
        session->prepare(engine::runtime::build_preparation_request(request));
        const auto result = offline->run(request);
        std::ostringstream json;
        write_task_result_json(json, result, engine::runtime::to_string(spec.task));
        *out_json = copy_string(json.str()); return AUDIOCPP_OK;
    } catch (const std::invalid_argument & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_BAD_ARG;
    } catch (const std::exception & exception) { set_error(err, errlen, exception.what()); return AUDIOCPP_ERR_INFERENCE_FAILED;
    } catch (...) { set_error(err, errlen, "unknown inference failure"); return AUDIOCPP_ERR_INFERENCE_FAILED; }
}

AUDIOCPP_API int32_t audiocpp_model_run_json(
    audiocpp_model * context, const char * task, const char * text,
    const float * audio_samples, int32_t audio_count, int32_t audio_sample_rate,
    int32_t audio_channels, const char * voice_id, const float * ref_pcm,
    int32_t ref_count, int32_t ref_sample_rate, const char * options_json,
    char ** out_json, char * err, size_t errlen) {
    return audiocpp_model_run_json_ex(context, task, text, nullptr, audio_samples, audio_count, audio_sample_rate,
        audio_channels, voice_id, ref_pcm, ref_count, ref_sample_rate, nullptr, options_json,
        out_json, err, errlen);
}

AUDIOCPP_API int32_t audiocpp_stream_open(
    audiocpp_model * context, const char * task, const char * options_json,
    audiocpp_stream ** out_stream, char ** out_info, char * err, size_t errlen) {
    return open_stream_impl(context, task, nullptr, nullptr, nullptr, options_json, out_stream, out_info, err, errlen);
}

AUDIOCPP_API int32_t audiocpp_stream_open_ex(
    audiocpp_model * context, const char * task, const char * text, const char * text_language,
    const char * artifacts_json, const char * options_json,
    audiocpp_stream ** out_stream, char ** out_info, char * err, size_t errlen) {
    return open_stream_impl(context, task, text, text_language, artifacts_json, options_json,
        out_stream, out_info, err, errlen);
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
        const auto mono = make_audio_buffer(samples, sample_count, sample_rate, channels);
        engine::runtime::AudioChunk chunk;
        chunk.sample_rate = mono.sample_rate;
        chunk.channels = mono.channels;
        chunk.start_sample = stream->samples_pushed;
        chunk.samples = mono.samples;
        stream->samples_pushed += mono.channels == channels ? sample_count : static_cast<int64_t>(mono.samples.size());
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
        write_task_result_json(json, result, stream->task.c_str());
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
