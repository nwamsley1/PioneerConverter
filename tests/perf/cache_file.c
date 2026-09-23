// macOS per-file cache inspection/eviction for controlled cold-client benchmarks.
// Build: cc -O2 -Wall -Wextra -Werror tests/perf/cache_file.c -o /tmp/pioneer-cache-file
// No file payload is read, no file contents are written, and no persistent flags
// are changed. Only an explicitly named regular file is mapped read-only.
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#ifdef __APPLE__
typedef struct {
    uint64_t resident_pages;
    uint64_t resident_bytes;
    uint64_t paged_out_pages;
} residency;

static void json_string(const char *value)
{
    putchar('"');
    for (const unsigned char *p = (const unsigned char *)value; *p; ++p) {
        if (*p == '"' || *p == '\\') printf("\\%c", *p);
        else if (*p < 0x20) printf("\\u%04x", *p);
        else putchar(*p);
    }
    putchar('"');
}

static int inspect(void *mapping, size_t size, size_t page_size, size_t pages,
                   char *vector, residency *result)
{
    memset(result, 0, sizeof(*result));
    if (!size) return 0;
    if (mincore(mapping, size, vector) != 0) return -1;
    for (size_t i = 0; i < pages; ++i) {
        if (vector[i] & MINCORE_INCORE) {
            result->resident_pages++;
            size_t remainder = size - i * page_size;
            result->resident_bytes += remainder < page_size ? remainder : page_size;
        }
        if (vector[i] & MINCORE_PAGED_OUT) result->paged_out_pages++;
    }
    return 0;
}

static void print_residency(residency value)
{
    printf("{\"resident_pages\":%" PRIu64 ",\"resident_bytes\":%" PRIu64
           ",\"paged_out_pages\":%" PRIu64 "}",
           value.resident_pages, value.resident_bytes, value.paged_out_pages);
}

static int64_t mtime_ns(struct stat value)
{
    return (int64_t)value.st_mtimespec.tv_sec * INT64_C(1000000000) + value.st_mtimespec.tv_nsec;
}

int main(int argc, char **argv)
{
    if (argc != 3 || (strcmp(argv[1], "inspect") && strcmp(argv[1], "evict"))) {
        fprintf(stderr, "Usage: cache_file inspect|evict RAW_PATH\n");
        return 2;
    }
    const bool evict = !strcmp(argv[1], "evict");
    int result = 1;
    int fd = open(argv[2], O_RDONLY | O_CLOEXEC);
    if (fd < 0) { perror("open read-only"); return 1; }
    void *mapping = MAP_FAILED;
    char *vector = NULL;
    struct stat before, after;
    if (fstat(fd, &before) != 0) { perror("fstat before"); goto cleanup; }
    if (!S_ISREG(before.st_mode) || before.st_size < 0 || (uint64_t)before.st_size > SIZE_MAX) {
        fprintf(stderr, "Input must be a regular file representable in this process.\n");
        goto cleanup;
    }
    const size_t size = (size_t)before.st_size;
    const long system_page_size = sysconf(_SC_PAGESIZE);
    if (system_page_size <= 0) { fprintf(stderr, "Invalid system page size.\n"); goto cleanup; }
    const size_t page_size = (size_t)system_page_size;
    const size_t pages = size / page_size + (size % page_size != 0);
    if (size) {
        mapping = mmap(NULL, size, PROT_READ, MAP_SHARED, fd, 0);
        if (mapping == MAP_FAILED) { perror("mmap read-only"); goto cleanup; }
        vector = malloc(pages);
        if (vector == NULL) { perror("allocate mincore vector"); goto cleanup; }
    }
    residency prior, subsequent;
    if (inspect(mapping, size, page_size, pages, vector, &prior) != 0) {
        perror("mincore before"); goto cleanup;
    }
    int eviction_errno = 0;
    // Darwin maps MS_INVALIDATE alone to synchronous VM invalidation. This is
    // the same per-file operation used by vmtouch -e on macOS. Do not touch any
    // mapped byte: mincore observes existing residency without warming payloads.
    if (evict && size && msync(mapping, size, MS_INVALIDATE) != 0) eviction_errno = errno;
    if (inspect(mapping, size, page_size, pages, vector, &subsequent) != 0) {
        perror("mincore after"); goto cleanup;
    }
    if (fstat(fd, &after) != 0) { perror("fstat after"); goto cleanup; }
    const bool unchanged = before.st_size == after.st_size && mtime_ns(before) == mtime_ns(after) &&
        before.st_dev == after.st_dev && before.st_ino == after.st_ino;
    const bool cold = !eviction_errno && unchanged && !subsequent.resident_pages && !subsequent.paged_out_pages;
    printf("{\"operation\":"); json_string(argv[1]);
    printf(",\"path\":"); json_string(argv[2]);
    printf(",\"size_bytes\":%zu,\"page_size\":%zu,\"total_pages\":%zu,\"before\":", size, page_size, pages);
    print_residency(prior);
    printf(",\"after\":"); print_residency(subsequent);
    printf(",\"device_before\":%" PRIuMAX ",\"device_after\":%" PRIuMAX
           ",\"inode_before\":%" PRIuMAX ",\"inode_after\":%" PRIuMAX
           ",\"mtime_before_ns\":%" PRId64 ",\"mtime_after_ns\":%" PRId64
           ",\"metadata_unchanged\":%s,\"eviction_errno\":%d,\"cold_client_verified\":%s}\n",
           (uintmax_t)before.st_dev, (uintmax_t)after.st_dev, (uintmax_t)before.st_ino, (uintmax_t)after.st_ino,
           mtime_ns(before), mtime_ns(after), unchanged ? "true" : "false", eviction_errno, cold ? "true" : "false");
    if (eviction_errno) fprintf(stderr, "msync(MS_INVALIDATE): %s\n", strerror(eviction_errno));
    if (!unchanged) fprintf(stderr, "File identity/size/mtime changed during cache inspection.\n");
    if (evict && !cold) fprintf(stderr, "Cold-client precondition failed; do not label the next run cold.\n");
    result = !unchanged || (evict && !cold) ? 1 : 0;
cleanup:
    free(vector);
    if (mapping != MAP_FAILED && munmap(mapping, (size_t)before.st_size) != 0) { perror("munmap"); result = 1; }
    if (close(fd) != 0) { perror("close"); result = 1; }
    return result;
}
#else
int main(void)
{
    fprintf(stderr, "cache_file supports Darwin/macOS only; do not assume MS_INVALIDATE evicts on other operating systems.\n");
    return 2;
}
#endif
