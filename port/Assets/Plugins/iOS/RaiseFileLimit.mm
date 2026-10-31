// iOS port: the rebuilt content uses thousands of small asset bundles, and Unity keeps LZ4 bundles
// open while their assets are loaded. Raise the per-process open-file limit before Unity starts so
// large scenes can't fail with "Too many open files".
#include <sys/resource.h>
#include <algorithm>

__attribute__((constructor)) static void IOSPortRaiseFileLimit()
{
    struct rlimit limit;
    if (getrlimit(RLIMIT_NOFILE, &limit) != 0)
        return;
    rlim_t wanted = 10240; // OPEN_MAX on iOS
    limit.rlim_cur = limit.rlim_max == RLIM_INFINITY ? wanted : std::min(wanted, limit.rlim_max);
    setrlimit(RLIMIT_NOFILE, &limit);
}
