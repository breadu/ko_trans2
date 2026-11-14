cmake_minimum_required(VERSION 3.20)
project(KoTransOpenCvSharpExtern LANGUAGES CXX)

find_package(OpenCV 4.11.0 EXACT REQUIRED COMPONENTS core imgproc imgcodecs)

set(wrapper "${OPENCVSHARP_SOURCE}/src/OpenCvSharpExtern")
add_library(OpenCvSharpExtern SHARED
    "${wrapper}/core.cpp"
    "${wrapper}/imgproc.cpp"
    "${wrapper}/imgcodecs.cpp"
    "${wrapper}/std_string.cpp"
    "${wrapper}/std_vector.cpp")
target_include_directories(OpenCvSharpExtern PRIVATE ${OpenCV_INCLUDE_DIRS})
target_link_libraries(OpenCvSharpExtern PRIVATE ${OpenCV_LIBS})
target_compile_features(OpenCvSharpExtern PRIVATE cxx_std_17)
set_property(TARGET OpenCvSharpExtern PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
install(TARGETS OpenCvSharpExtern RUNTIME DESTINATION .)
