/*
 * End-to-end probe: drives real inference through the pinned C ABI without any
 * managed code, so the same binary proves every matrix cell (win/linux x
 * cpu/cuda). One WAV reader, three modes:
 *
 *   audiocpp_dotnet_e2e asr MODEL WAV [--family F] [--backend B] [--threads N]
 *   audiocpp_dotnet_e2e tts MODEL TEXT OUT.wav [--voice-ref WAV] [--ref-text T] [--max-tokens N] [--family F] [--backend B] [--threads N]
 *   audiocpp_dotnet_e2e vad MODEL WAV [--family F] [--backend B] [--threads N]
 *
 * Exit 0 means the model loaded, inference ran and the outputs made sense.
 * Progress lines are prefixed "e2e:" so matrix logs can grep them.
 */
#include "audiocpp_dotnet.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {

struct WavData {
    std::vector<float> samples;  // interleaved
    int32_t sample_rate = 0;
    int32_t channels = 1;
};

inline uint32_t read_u32(const uint8_t * p) { return p[0] | p[1] << 8 | p[2] << 16 | uint32_t(p[3]) << 24; }
inline uint16_t read_u16(const uint8_t * p) { return uint16_t(p[0] | p[1] << 8); }

bool load_wav(const std::string & path, WavData & out, std::string & error) {
    std::ifstream file(path, std::ios::binary);
    if (!file) { error = "cannot open " + path; return false; }
    uint8_t header[12];
    file.read(reinterpret_cast<char *>(header), sizeof(header));
    if (!file || std::memcmp(header, "RIFF", 4) != 0 || std::memcmp(header + 8, "WAVE", 4) != 0) {
        error = path + " is not a RIFF/WAVE file";
        return false;
    }
    uint16_t format = 0, channels = 0, block = 0;
    uint32_t rate = 0;
    bool have_fmt = false;
    std::vector<uint8_t> data;
    while (file) {
        uint8_t chunk[8];
        file.read(reinterpret_cast<char *>(chunk), sizeof(chunk));
        if (!file || file.gcount() < 8) break;
        const uint32_t size = read_u32(chunk + 4);
        if (std::memcmp(chunk, "fmt ", 4) == 0) {
            std::vector<uint8_t> fmt((std::min)(size, uint32_t(64)));
            file.read(reinterpret_cast<char *>(fmt.data()), std::streamsize(fmt.size()));
            format = read_u16(fmt.data());
            channels = read_u16(fmt.data() + 2);
            rate = read_u32(fmt.data() + 4);
            block = read_u16(fmt.data() + 12);
            have_fmt = true;
            file.seekg(std::streamoff(size) - std::streamoff(fmt.size()) + (size % 2), std::ios::cur);
        } else if (std::memcmp(chunk, "data", 4) == 0) {
            data.resize(size);
            file.read(reinterpret_cast<char *>(data.data()), std::streamsize(size));
            if (file.gcount() < std::streamsize(size)) data.resize(size_t(file.gcount()));
            if (size % 2 == 1) file.get();
        } else {
            file.seekg(std::streamoff(size) + (size % 2), std::ios::cur);
        }
    }
    if (!have_fmt || data.empty()) { error = path + " lacks fmt/data chunks"; return false; }
    if (channels == 0 || rate == 0) { error = path + " has an invalid fmt chunk"; return false; }
    out.sample_rate = int32_t(rate);
    out.channels = channels;
    if (format == 1 && block / channels == 2) {  // PCM16
        out.samples.resize(data.size() / 2);
        for (size_t i = 0; i < out.samples.size(); ++i)
            out.samples[i] = int16_t(data[i * 2] | data[i * 2 + 1] << 8) / 32768.0f;
    } else if (format == 3 && block / channels == 4) {  // IEEE float32
        out.samples.resize(data.size() / 4);
        std::memcpy(out.samples.data(), data.data(), data.size() - data.size() % 4);
    } else {
        error = path + ": only PCM16 and float32 WAV are supported (format=" +
                std::to_string(format) + ", block=" + std::to_string(block) + ")";
        return false;
    }
    return true;
}

void write_wav(const std::string & path, const float * samples, size_t count, int32_t rate, int32_t channels) {
    std::ofstream file(path, std::ios::binary);
    const uint32_t bytes = uint32_t(count * sizeof(float));
    file.write("RIFF", 4);
    uint8_t u32[4];
    const uint32_t riff = uint32_t(36 + bytes);
    u32[0] = riff & 0xff; u32[1] = riff >> 8 & 0xff; u32[2] = riff >> 16 & 0xff; u32[3] = riff >> 24 & 0xff;
    file.write(reinterpret_cast<const char *>(u32), 4);
    file.write("WAVEfmt ", 8);
    uint8_t fmt[16] = {};
    fmt[0] = 3;  // IEEE float
    fmt[2] = uint8_t(channels); fmt[3] = uint8_t(channels >> 8);
    fmt[4] = uint8_t(rate); fmt[5] = uint8_t(rate >> 8); fmt[6] = uint8_t(rate >> 16); fmt[7] = uint8_t(rate >> 24);
    const uint32_t byte_rate = uint32_t(rate) * uint32_t(channels) * 4;
    fmt[8] = uint8_t(byte_rate); fmt[9] = uint8_t(byte_rate >> 8); fmt[10] = uint8_t(byte_rate >> 16); fmt[11] = uint8_t(byte_rate >> 24);
    fmt[12] = uint8_t(channels * 4); fmt[13] = uint8_t((channels * 4) >> 8);
    fmt[14] = 32; fmt[15] = 0;
    file.write(reinterpret_cast<const char *>(fmt), sizeof(fmt));
    file.write("data", 4);
    u32[0] = bytes & 0xff; u32[1] = bytes >> 8 & 0xff; u32[2] = bytes >> 16 & 0xff; u32[3] = bytes >> 24 & 0xff;
    file.write(reinterpret_cast<const char *>(u32), 4);
    file.write(reinterpret_cast<const char *>(samples), std::streamsize(bytes));
}

struct Args {
    std::string mode, model, positional, out_wav, voice_ref, ref_text, family, backend;
    int threads = 0;
    int max_tokens = 0;
};

Args parse(int argc, char ** argv) {
    Args args;
    if (argc >= 2) args.mode = argv[1];
    if (argc >= 3) args.model = argv[2];
    std::vector<std::string> positional;
    for (int i = 3; i < argc; ++i) {
        const std::string flag = argv[i];
        auto value = [&]() -> std::string { return i + 1 < argc ? argv[++i] : std::string(); };
        if (flag == "--family") args.family = value();
        else if (flag == "--backend") args.backend = value();
        else if (flag == "--voice-ref") args.voice_ref = value();
        else if (flag == "--ref-text") args.ref_text = value();
        else if (flag == "--threads") args.threads = std::atoi(value().c_str());
        else if (flag == "--max-tokens") args.max_tokens = std::atoi(value().c_str());
        else positional.emplace_back(flag);
    }
    if (args.mode == "tts") {
        if (!positional.empty()) args.positional = positional[0];
        if (positional.size() > 1 && args.out_wav.empty()) args.out_wav = positional[1];
    } else if (!positional.empty()) {
        args.positional = positional[0];
    }
    return args;
}

audiocpp_model * load(const Args & args, char * err, size_t errlen) {
    return audiocpp_model_load(args.model.c_str(),
        args.family.empty() ? nullptr : args.family.c_str(),
        args.backend.empty() ? nullptr : args.backend.c_str(),
        0, args.threads, nullptr, err, errlen);
}

int count_key(const std::string & json, const char * key) {
    int hits = 0;
    size_t at = 0;
    const size_t len = std::strlen(key);
    while ((at = json.find(key, at)) != std::string::npos) { ++hits; at += len; }
    return hits;
}

}  // namespace

// --- mode: asr ---------------------------------------------------------------

int run_asr(const Args & args) {
    WavData wav;
    std::string error;
    if (!load_wav(args.positional, wav, error)) { std::cerr << "e2e: " << error << '\n'; return 2; }
    char err[512] = {};
    audiocpp_model * model = load(args, err, sizeof(err));
    if (model == nullptr) { std::cerr << "e2e: load failed: " << err << '\n'; return 3; }

    char * text = nullptr;
    int status = audiocpp_model_transcribe(model, wav.samples.data(), int32_t(wav.samples.size()),
        wav.sample_rate, wav.channels, nullptr, &text, err, sizeof(err));
    if (status != AUDIOCPP_OK) {
        std::cerr << "e2e: transcribe failed: " << err << '\n';
        audiocpp_model_free(model);
        return 4;
    }
    std::cout << "e2e: asr text: " << (text ? text : "") << '\n';
    const bool has_text = text != nullptr && *text != '\0';
    audiocpp_buffer_free(text);

    char * json = nullptr;
    status = audiocpp_model_run_json(model, "asr", nullptr, wav.samples.data(), int32_t(wav.samples.size()),
        wav.sample_rate, wav.channels, nullptr, nullptr, 0, 0, nullptr, &json, err, sizeof(err));
    if (status != AUDIOCPP_OK) {
        std::cerr << "e2e: run_json failed: " << err << '\n';
        audiocpp_model_free(model);
        return 5;
    }
    const std::string result = json ? json : "";
    audiocpp_buffer_free(json);
    const int schema = count_key(result, "\"schema_version\"");
    const int task_echo = count_key(result, "\"task\":\"asr\"");
    std::cout << "e2e: structured bytes=" << result.size() << " schema_ok=" << (schema > 0)
              << " task_echo=" << task_echo << '\n';
    audiocpp_model_free(model);
    if (schema == 0 || task_echo != 1) { std::cerr << "e2e: structured result malformed\n"; return 6; }
    if (!has_text) { std::cerr << "e2e: empty transcript\n"; return 7; }
    return 0;
}

// --- mode: tts ---------------------------------------------------------------

int run_tts(const Args & args) {
    if (args.out_wav.empty()) { std::cerr << "e2e: tts needs TEXT and OUT.wav\n"; return 2; }
    WavData ref;
    std::string error;
    const float * ref_pcm = nullptr;
    int32_t ref_rate = 0;
    if (!args.voice_ref.empty()) {
        if (!load_wav(args.voice_ref, ref, error)) { std::cerr << "e2e: " << error << '\n'; return 2; }
        ref_pcm = ref.samples.data();
        ref_rate = ref.sample_rate;
    }
    char err[512] = {};
    audiocpp_model * model = load(args, err, sizeof(err));
    if (model == nullptr) { std::cerr << "e2e: load failed: " << err << '\n'; return 3; }
    float * samples = nullptr;
    int32_t count = 0, rate = 0, channels = 0;
    // Qwen3 voice-clone ICL mode demands the reference transcript alongside the prompt
    // audio. max_tokens caps the talker's codec-frame budget: the loader default is
    // 2048 frames, which samples an EOS on a fast CUDA run but makes a CPU-only run
    // grind for hours (and, with a long prompt, blow past the RAM ceiling), so the
    // matrix pins a small budget for a bounded smoke test.
    std::vector<std::string> fields;
    if (!args.ref_text.empty()) fields.push_back("\"reference_text\":\"" + args.ref_text + "\"");
    if (args.max_tokens > 0) fields.push_back("\"max_tokens\":" + std::to_string(args.max_tokens));
    std::string options;
    if (!fields.empty()) {
        options = "{";
        for (size_t i = 0; i < fields.size(); ++i) { if (i != 0) options += ','; options += fields[i]; }
        options += "}";
    }
    const int status = audiocpp_model_synthesize(model, "tts", args.positional.c_str(), nullptr,
        ref_pcm, ref_pcm ? int32_t(ref.samples.size()) : 0, ref_rate,
        options.empty() ? nullptr : options.c_str(),
        &samples, &count, &rate, &channels, err, sizeof(err));
    audiocpp_model_free(model);
    if (status != AUDIOCPP_OK) { std::cerr << "e2e: synthesize failed: " << err << '\n'; return 4; }
    std::cout << "e2e: tts samples=" << count << " rate=" << rate << " channels=" << channels << '\n';
    const bool audible = count > 0 && rate > 0 && samples != nullptr;
    if (audible) write_wav(args.out_wav, samples, size_t(count), rate, channels);
    audiocpp_buffer_free(samples);
    if (!audible) { std::cerr << "e2e: empty synthesis\n"; return 5; }
    return 0;
}

// --- mode: vad ---------------------------------------------------------------

int extract_int(const std::string & json, const char * key, int fallback) {
    const std::string needle = std::string("\"") + key + "\":";
    const size_t at = json.find(needle);
    if (at == std::string::npos) return fallback;
    return std::atoi(json.c_str() + at + needle.size());
}

int run_vad(const Args & args) {
    WavData wav;
    std::string error;
    if (!load_wav(args.positional, wav, error)) { std::cerr << "e2e: " << error << '\n'; return 2; }
    char err[512] = {};
    audiocpp_model * model = load(args, err, sizeof(err));
    if (model == nullptr) { std::cerr << "e2e: load failed: " << err << '\n'; return 3; }

    audiocpp_stream * stream = nullptr;
    char * info = nullptr;
    int status = audiocpp_stream_open(model, "vad", nullptr, &stream, &info, err, sizeof(err));
    if (status != AUDIOCPP_OK) {
        std::cerr << "e2e: stream open failed: " << err << '\n';
        audiocpp_model_free(model);
        return 4;
    }
    const std::string policy = info ? info : "";
    audiocpp_buffer_free(info);
    int chunk = extract_int(policy, "preferred_chunk_samples", 0);
    if (chunk <= 0) chunk = 512;  // fixed-window fallback; adaptive loaders accept any size
    std::cout << "e2e: vad policy chunk=" << chunk << '\n';

    int starts = 0, ends = 0;
    const std::vector<float> & samples = wav.samples;
    for (size_t offset = 0; offset < samples.size(); offset += size_t(chunk)) {
        const size_t length = (std::min)(samples.size() - offset, size_t(chunk));
        std::vector<float> block;
        const float * data = samples.data() + offset;
        int32_t count = int32_t(length);
        if (length < size_t(chunk)) {  // zero-pad the tail to keep the window aligned
            block.assign(size_t(chunk), 0.0f);
            std::copy(samples.begin() + std::ptrdiff_t(offset), samples.end(), block.begin());
            data = block.data();
            count = chunk;
        }
        char * events = nullptr;
        status = audiocpp_stream_push_pcm(stream, data, count, wav.sample_rate, wav.channels, &events, err, sizeof(err));
        if (status != AUDIOCPP_OK) {
            std::cerr << "e2e: push failed at offset " << offset << ": " << err << '\n';
            audiocpp_stream_free(stream);
            audiocpp_model_free(model);
            return 5;
        }
        if (events != nullptr) {
            starts += count_key(events, "\"speech_start\"");
            ends += count_key(events, "\"speech_end\"");
        }
        audiocpp_buffer_free(events);
    }
    char * final_json = nullptr;
    status = audiocpp_stream_finish(stream, &final_json, err, sizeof(err));
    audiocpp_stream_free(stream);
    audiocpp_model_free(model);
    if (status != AUDIOCPP_OK) { std::cerr << "e2e: finish failed: " << err << '\n'; return 6; }
    const std::string result = final_json ? final_json : "";
    audiocpp_buffer_free(final_json);
    const int schema = count_key(result, "\"schema_version\"");
    const int segments = count_key(result, "\"speech_segments\"");
    std::cout << "e2e: vad events start=" << starts << " end=" << ends
              << " final_bytes=" << result.size() << " schema_ok=" << (schema > 0) << '\n';
    if (schema == 0) { std::cerr << "e2e: final result malformed\n"; return 7; }
    if (starts == 0 && ends == 0 && segments == 0) {
        std::cerr << "e2e: no voice activity detected\n";
        return 8;
    }
    return 0;
}

#if defined(_WIN32)
#include <windows.h>
#include <shellapi.h>

// Windows argv arrives encoded in the active code page (e.g. GBK), which breaks
// non-ASCII TTS text. Enter through wmain and narrow to UTF-8 explicitly.
static std::vector<std::string> narrow_utf8(const int argc, wchar_t ** wargv) {
    std::vector<std::string> out;
    out.reserve(static_cast<size_t>(argc));
    for (int i = 0; i < argc; ++i) {
        const int bytes = WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, nullptr, 0, nullptr, nullptr);
        std::string converted(bytes > 1 ? bytes - 1 : 0, '\0');
        if (bytes > 1)
            WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, &converted[0], bytes, nullptr, nullptr);
        out.push_back(std::move(converted));
    }
    return out;
}
#endif

static int run(const int argc, char ** argv) {
    if (argc < 3) {
        std::cerr << "usage:\n"
                  << "  audiocpp_dotnet_e2e asr MODEL WAV [--family F] [--backend B] [--threads N]\n"
                  << "  audiocpp_dotnet_e2e tts MODEL TEXT OUT.wav [--voice-ref WAV] [--ref-text TEXT] [--max-tokens N] [--family F] [--backend B] [--threads N]\n"
                  << "  audiocpp_dotnet_e2e vad MODEL WAV [--family F] [--backend B] [--threads N]\n";
        return 2;
    }
    const Args args = parse(argc, argv);
    if (args.mode == "asr") return run_asr(args);
    if (args.mode == "tts") return run_tts(args);
    if (args.mode == "vad") return run_vad(args);
    std::cerr << "e2e: unknown mode " << args.mode << '\n';
    return 2;
}

#if defined(_WIN32)
// Only wmain exists on Windows so the linker selects wmainCRTStartup; defining
// main alongside it would make MSVC pick the ANSI entry and lose non-ASCII argv.
int wmain(int argc, wchar_t ** wargv) {
    SetConsoleOutputCP(CP_UTF8);
    const std::vector<std::string> narrowed = narrow_utf8(argc, wargv);
    std::vector<char *> pointers;
    pointers.reserve(narrowed.size());
    for (const auto & value : narrowed) pointers.push_back(const_cast<char *>(value.c_str()));
    return run(static_cast<int>(pointers.size()), pointers.data());
}
#else
int main(int argc, char ** argv) { return run(argc, argv); }
#endif