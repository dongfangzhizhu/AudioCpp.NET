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
    if (info.abi_major != 1 || info.abi_minor != 0) return 2;
    if (std::strcmp(info.audio_cpp_commit, "78d47706c30ef215ba9ad3559baff309efeb5260") != 0) return 3;
    if (info.shim_version == nullptr || info.backend == nullptr) return 4;
    std::cout << info.shim_version << "\naudio.cpp " << info.audio_cpp_commit << "\nbackend " << info.backend << '\n';
    return 0;
}
