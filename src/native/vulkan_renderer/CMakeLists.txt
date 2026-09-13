cmake_minimum_required(VERSION 3.22.1)
project(xla_vulkan_renderer C CXX)

# GameNative's libvulkan_renderer (app/src/main/cpp/winlator: Vulkan* + window.vert/.frag), vendored so the
# compositor shader can carry extra upscalers (Snapdragon GSR). libadrenotools is GameNative's pinned fork
# (Pipetto-crypto/libadrenotools @ 8483dfd), linked statically like GameNative's prebuilt.
# Build with scripts/build-native.sh.
set(ADRENOTOOLS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../../sources/libadrenotools-gn" CACHE PATH "libadrenotools checkout")
add_subdirectory(${ADRENOTOOLS_DIR} adrenotools EXCLUDE_FROM_ALL)

find_program(GLSLANG_VALIDATOR glslangValidator REQUIRED)

add_custom_command(
    OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/window_vert.h
    COMMAND ${GLSLANG_VALIDATOR} -V --vn window_vert_code -x
            -o ${CMAKE_CURRENT_BINARY_DIR}/window_vert.h
            ${CMAKE_CURRENT_SOURCE_DIR}/window.vert
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/window.vert
)

add_custom_command(
    OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/window_frag.h
    COMMAND ${GLSLANG_VALIDATOR} -V --vn window_frag_code -x
            -o ${CMAKE_CURRENT_BINARY_DIR}/window_frag.h
            ${CMAKE_CURRENT_SOURCE_DIR}/window.frag
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/window.frag
)

add_library(vulkan_renderer SHARED
            vulkan_jni.cpp
            VulkanRendererContext.cpp
            VulkanRendererScanout.cpp
            ${CMAKE_CURRENT_BINARY_DIR}/window_vert.h
            ${CMAKE_CURRENT_BINARY_DIR}/window_frag.h)

target_include_directories(vulkan_renderer PRIVATE ${CMAKE_CURRENT_BINARY_DIR} ${ADRENOTOOLS_DIR}/include)

target_compile_options(vulkan_renderer PRIVATE
                       -O2 -Wall -Wextra -fvisibility=hidden -std=c++17)

target_link_libraries(vulkan_renderer
                      log
                      android
                      vulkan
                      adrenotools
                      dl
                      atomic)

target_link_options(vulkan_renderer PRIVATE -Wl,-z,max-page-size=16384)
