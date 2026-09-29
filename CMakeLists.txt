cmake_minimum_required(VERSION 3.25)
project(GlideNative VERSION 0.1.0 LANGUAGES CXX)
if(NOT WIN32)
  message(FATAL_ERROR "The capture engine requires Windows SDK and an interactive Windows desktop for runtime tests.")
endif()
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_library(Glide.Capture SHARED native/Glide.Capture/glide_capture.cpp)
target_compile_definitions(Glide.Capture PRIVATE UNICODE _UNICODE)
target_compile_options(Glide.Capture PRIVATE /W4 /WX /utf-8)
target_link_libraries(Glide.Capture PRIVATE windowsapp d3d11 d3dcompiler dxgi dwmapi mfplat mfreadwrite mfuuid ole32 user32 gdi32 avrt)
add_executable(glide-capture-probe native/Glide.Capture/capture_probe.cpp)
target_compile_definitions(glide-capture-probe PRIVATE UNICODE _UNICODE)
target_compile_options(glide-capture-probe PRIVATE /W4 /WX /utf-8)
target_link_libraries(glide-capture-probe PRIVATE Glide.Capture windowsapp ole32 user32 gdi32 dwmapi avrt)

add_executable(recording-timeline-test tests/native/recording_timeline_test.cpp)
target_compile_options(recording-timeline-test PRIVATE /W4 /WX)
target_include_directories(recording-timeline-test PRIVATE native/Glide.Capture)

add_executable(audio-queue-test tests/native/audio_queue_test.cpp)
target_compile_options(audio-queue-test PRIVATE /W4 /WX)
target_include_directories(audio-queue-test PRIVATE native/Glide.Capture)

# WGC probes require the selected interactive Windows VM / hardware.
# Headless ctest is not evidence of capture functionality.
