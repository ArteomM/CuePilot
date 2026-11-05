// cpw_test MODEL WAV LANGUAGE [AUDIO_CTX]: recognizes a 16 kHz mono 16-bit WAV with the CuePilot C layer and prints
// the text and the time. AUDIO_CTX "auto" sizes the encoder window to the audio like the phone does for a recording.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct cpw_context cpw_context;
cpw_context * cpw_init(const char * model_path);
int cpw_transcribe(cpw_context * context, const float * samples, int count, const char * language, const char * prompt, int threads, int audio_ctx);
const char * cpw_text(cpw_context * context);
void cpw_free(cpw_context * context);

static double now_seconds(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

int main(int argc, char ** argv) {
    if (argc < 4) {
        fprintf(stderr, "usage: cpw_test MODEL WAV LANGUAGE [AUDIO_CTX|auto]\n");
        return 2;
    }

    FILE * file = fopen(argv[2], "rb");
    if (file == NULL) {
        fprintf(stderr, "cannot open %s\n", argv[2]);
        return 1;
    }

    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    unsigned char * bytes = malloc(size);
    if (bytes == NULL || fread(bytes, 1, size, file) != (size_t) size) {
        fprintf(stderr, "cannot read %s\n", argv[2]);
        return 1;
    }
    fclose(file);

    // Find the "data" chunk of the RIFF file.
    long offset = 12;
    long data = -1;
    long data_size = 0;
    while (offset + 8 <= size) {
        long chunk_size = bytes[offset + 4] | (bytes[offset + 5] << 8) | (bytes[offset + 6] << 16) | ((long) bytes[offset + 7] << 24);
        if (memcmp(bytes + offset, "data", 4) == 0) {
            data = offset + 8;
            data_size = chunk_size < size - data ? chunk_size : size - data;
            break;
        }
        offset += 8 + chunk_size + (chunk_size & 1);
    }

    if (data < 0) {
        fprintf(stderr, "no data chunk in %s\n", argv[2]);
        return 1;
    }

    int count = (int) (data_size / 2);
    float * samples = malloc(sizeof(float) * count);
    for (int i = 0; i < count; i++) {
        short value = (short) (bytes[data + 2 * i] | (bytes[data + 2 * i + 1] << 8));
        samples[i] = value / 32768.0f;
    }

    int audio_ctx = 0;
    if (argc > 4) {
        audio_ctx = strcmp(argv[4], "auto") == 0 ? ((count / 16000 + 2) * 50 + 31) / 32 * 32 : atoi(argv[4]);
        if (audio_ctx > 1500) {
            audio_ctx = 0;
        }
    }

    double started = now_seconds();
    cpw_context * context = cpw_init(argv[1]);
    if (context == NULL) {
        fprintf(stderr, "model did not load\n");
        return 1;
    }

    double loaded = now_seconds();
    int result = cpw_transcribe(context, samples, count, argv[3], "", 4, audio_ctx);
    double done = now_seconds();
    printf("audio %.1f s, audio_ctx %d, load %.2f s, recognition %.2f s, result %d\n", count / 16000.0, audio_ctx, loaded - started, done - loaded, result);
    printf("TEXT: %s\n", cpw_text(context));
    cpw_free(context);
    return result;
}
