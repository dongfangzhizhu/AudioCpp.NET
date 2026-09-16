#include "audiocpp_dotnet.h"

#include <cstring>
#include <iostream>

namespace {
bool contains(const char * haystack, const char * needle) {
    return haystack != nullptr && std::strstr(haystack, needle) != nullptr;
}
}  // namespace

// The managed reader parses both audiocpp_model_run_json_ex and
// audiocpp_stream_finish with one type, and reads the "task" echo added in schema
// 2. Bump this assert whenever the schema changes so the two stay in step.
static_assert(AUDIOCPP_STRUCTURED_RESULT_SCHEMA_VERSION == 2u,
              "managed AudioCppTaskResult expects structured result schema 2");
static_assert(AUDIOCPP_TASK_CATALOG_SCHEMA_VERSION == 1u,
              "managed CatalogJson.ParseTasks expects task catalog schema 1");

// Links against the shim and asserts the exported ABI surface: version, capability
// bits, task catalog completeness (canonical tokens plus model_spec aliases) and
// loader catalog reachability. Runs as part of the native build, so an ABI change
// that forgets the export list fails here instead of in managed code.
int main() {
    char error[512] = {};
    audiocpp_abi_info info = {};
    info.struct_size = sizeof(info);
    const int status = audiocpp_get_abi_info(&info, error, sizeof(error));
    if (status != AUDIOCPP_OK) {
        std::cerr << "ABI query failed: " << error << '\n';
        return 1;
    }
    if (info.abi_major != 1 || info.abi_minor != 3) return 2;
    if (std::strcmp(info.audio_cpp_commit, "78d47706c30ef215ba9ad3559baff309efeb5260") != 0) return 3;
    if (info.shim_version == nullptr || info.backend == nullptr) return 4;
    const uint64_t required = AUDIOCPP_CAP_SYNTHESIZE | AUDIOCPP_CAP_TRANSCRIBE |
        AUDIOCPP_CAP_STRUCTURED_RESULTS | AUDIOCPP_CAP_STREAMING |
        AUDIOCPP_CAP_TASK_CATALOG | AUDIOCPP_CAP_ARTIFACTS | AUDIOCPP_CAP_EXEC_OPTIONS;
    if ((info.capabilities & required) != required) return 7;

    char * catalog = nullptr;
    if (audiocpp_get_loader_catalog(&catalog, error, sizeof(error)) != AUDIOCPP_OK || catalog == nullptr) return 5;
    if (!contains(catalog, "\"loaders\"")) return 6;
    audiocpp_buffer_free(catalog);

    char * tasks = nullptr;
    if (audiocpp_get_task_catalog(&tasks, error, sizeof(error)) != AUDIOCPP_OK || tasks == nullptr) return 8;
    // Every canonical token must be published, and all eight model_spec aliases that
    // audiocpp_model_run_json accepts must be discoverable through it. Omitting an
    // alias here is how a model becomes unreachable from managed code.
    for (const char * token : {"\"vad\"", "\"asr\"", "\"diar\"", "\"sep\"", "\"gen\"", "\"tts\"", "\"clon\"",
                               "\"vc\"", "\"s2s\"", "\"align\"", "\"vdes\"", "\"spk\"", "\"svc\"", "\"midi\"",
                               "\"audio_generation\"", "\"music\"", "\"sfx\"", "\"edit\"",
                               "\"clone\"", "\"design\"", "\"speaker\"", "\"codec\""}) {
        if (!contains(tasks, token)) {
            std::cerr << "task catalog is missing " << token << '\n';
            audiocpp_buffer_free(tasks);
            return 9;
        }
    }
    audiocpp_buffer_free(tasks);

    // Null-model probes: the new entry points must exist and reject bad arguments
    // rather than crash, which also proves the symbols resolved at link time.
    char * json = nullptr;
    if (audiocpp_model_run_json_ex(nullptr, "tts", nullptr, nullptr, nullptr, 0, 0, 0, nullptr, nullptr, 0, 0,
                                   nullptr, nullptr, &json, error, sizeof(error)) != AUDIOCPP_ERR_BAD_ARG) return 10;
    audiocpp_stream * stream = nullptr;
    char * streamInfo = nullptr;
    if (audiocpp_stream_open_ex(nullptr, "vad", nullptr, nullptr, nullptr, nullptr,
                                &stream, &streamInfo, error, sizeof(error)) != AUDIOCPP_ERR_BAD_ARG) return 11;

    std::cout << info.shim_version << "\naudio.cpp " << info.audio_cpp_commit << "\nbackend " << info.backend
              << "\ncapabilities 0x" << std::hex << info.capabilities << std::dec << '\n';
    return 0;
}
