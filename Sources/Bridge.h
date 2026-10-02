// Private IOKit (IOMobileFramebuffer/DCP) symbols used for DDC/CI on Apple Silicon.
// Declared in C so Swift calls them with the C calling convention and ARC knows the
// create function returns a +1 reference (same signatures as m1ddc / MonitorControl).
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/IOKitLib.h>

typedef CFTypeRef IOAVServiceRef;

extern IOAVServiceRef _Nullable IOAVServiceCreateWithService(CFAllocatorRef _Nullable allocator, io_service_t service) CF_RETURNS_RETAINED;
extern IOReturn IOAVServiceReadI2C(IOAVServiceRef _Nonnull service, uint32_t chipAddress, uint32_t offset, void * _Nonnull outputBuffer, uint32_t outputBufferSize);
extern IOReturn IOAVServiceWriteI2C(IOAVServiceRef _Nonnull service, uint32_t chipAddress, uint32_t dataAddress, void * _Nonnull inputBuffer, uint32_t inputBufferSize);
