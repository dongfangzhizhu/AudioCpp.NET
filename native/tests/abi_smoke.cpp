#include "audiocpp_dotnet.h"

#include <cstring>
#include <iostream>

int main() {
    char error[512] = {};
    audiocpp_abi_info info = {};
    info.struct_size = sizeof(info);
    const int status = audiocpp_get_abi_info(&info, error, sizeof(error));
    if (status != AUDIOCPP_OK) {
        std::cerr << "ABI query failed: " << error << '\n';
        return 1;
    }
    if (info.abi_major != 1 || info.abi_minor != 2) return 2;
    if (std::strcmp(info.audio_cpp_commit, "78d47706c30ef215ba9ad3559baff309efeb5260") != 0) return 3;
    if (info.shim_version == nullptr || info.backend == nullptr) return 4;
    if ((info.capabilities & AUDIOCPP_CAP_STREAMING) == 0) return 7;
    char * catalog = nullptr;
    if (audiocpp_get_loader_catalog(&catalog, error, sizeof(error)) != AUDIOCPP_OK || catalog == nullptr) return 5;
    if (std::strstr(catalog, "qwen3_tts") == nullptr) return 6;
    audiocpp_buffer_free(catalog);
    std::cout << info.shim_version << "\naudio.cpp " << info.audio_cpp_commit << "\nbackend " << info.backend << '\n';
    return 0;
}
