# libcuepilot_whisper.so: whisper.cpp (static) plus the C layer in cuepilot_whisper.c, one library per CPU variant.
# Built by build-android.sh with the Android NDK; WHISPER_DIR points at a whisper.cpp checkout.
cmake_minimum_required(VERSION 3.14)
project(cuepilot_whisper C CXX)

set(WHISPER_DIR "" CACHE PATH "whisper.cpp source folder")
set(CPW_OUTPUT_NAME "cuepilot_whisper" CACHE STRING "Library name without lib and .so")

# Static whisper and ggml linked into one shared library.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(WHISPER_SDL2 OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
add_subdirectory(${WHISPER_DIR} whisper EXCLUDE_FROM_ALL)

add_library(cuepilot_whisper SHARED cuepilot_whisper.c)
set_target_properties(cuepilot_whisper PROPERTIES OUTPUT_NAME ${CPW_OUTPUT_NAME} C_VISIBILITY_PRESET hidden)
target_link_libraries(cuepilot_whisper PRIVATE whisper)
if(ANDROID)
  # 16 KB pages for Android 15+ devices; libc++ comes in statically (ANDROID_STL=c++_static in build-android.sh).
  target_link_options(cuepilot_whisper PRIVATE "-Wl,-z,max-page-size=16384")
endif()
