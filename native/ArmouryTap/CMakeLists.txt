cmake_minimum_required(VERSION 3.24)
project(AllyBindingsArmouryTap LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "ArmouryTap must be built as native Windows x64")
endif()

add_library(AllyBindingsArmouryTap SHARED
  src/ArmouryTap.cpp
  third_party/minhook/src/buffer.c
  third_party/minhook/src/hook.c
  third_party/minhook/src/trampoline.c
  third_party/minhook/src/hde/hde64.c)
target_compile_features(AllyBindingsArmouryTap PRIVATE cxx_std_20)
target_compile_definitions(AllyBindingsArmouryTap PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_include_directories(AllyBindingsArmouryTap PRIVATE
  third_party/minhook/include
  third_party/minhook/src
  third_party/minhook/src/hde)
target_link_libraries(AllyBindingsArmouryTap PRIVATE hid setupapi advapi32)
set_target_properties(AllyBindingsArmouryTap PROPERTIES
  OUTPUT_NAME "AllyBindings.ArmouryTap"
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(MSVC)
  target_compile_options(AllyBindingsArmouryTap PRIVATE /W4 /WX /guard:cf /sdl /O2 /GS)
  target_link_options(AllyBindingsArmouryTap PRIVATE /guard:cf /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /OPT:REF /OPT:ICF /Brepro)
elseif(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
  target_compile_options(AllyBindingsArmouryTap PRIVATE -Wall -Wextra -Werror)
  target_link_options(AllyBindingsArmouryTap PRIVATE -static -static-libgcc -static-libstdc++)
  target_compile_options(AllyBindingsArmouryTap PRIVATE
    $<$<COMPILE_LANGUAGE:CXX>:-Wno-pedantic> $<$<COMPILE_LANGUAGE:C>:-Wno-pedantic>)
endif()
