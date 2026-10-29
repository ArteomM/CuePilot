// Thin C layer over whisper.cpp for CuePilot on Android. C# calls these few functions instead of passing
// whisper.cpp's large parameter structs across P/Invoke: the parameters CuePilot uses are set here, the same
// ones DesktopCapture gives whisper-server on the computer (language, no timestamps, suppressed non-speech
// tokens, temperature 0, the Russian priming prompt with the glossary).
#include "whisper.h"

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define CPW_API __declspec(dllexport)
#else
#define CPW_API __attribute__((visibility("default")))
#endif

typedef struct cpw_context {
    struct whisper_context * whisper;
    atomic_int abort;
    char * text;
} cpw_context;

static void quiet_log(enum ggml_log_level level, const char * text, void * user_data) {
    (void) level;
    (void) text;
    (void) user_data;
}

static bool should_abort(void * user_data) {
    return atomic_load(&((cpw_context *) user_data)->abort) != 0;
}

CPW_API cpw_context * cpw_init(const char * model_path) {
    whisper_log_set(quiet_log, NULL);
    struct whisper_context_params params = whisper_context_default_params();
    params.use_gpu = false;
    struct whisper_context * whisper = whisper_init_from_file_with_params(model_path, params);
    if (whisper == NULL) {
        return NULL;
    }

    cpw_context * context = calloc(1, sizeof(cpw_context));
    if (context == NULL) {
        whisper_free(whisper);
        return NULL;
    }

    context->whisper = whisper;
    return context;
}

// Recognizes 16 kHz mono float samples. Returns 0 on success; the text is then read with cpw_text.
CPW_API int cpw_transcribe(cpw_context * context, const float * samples, int count, const char * language, const char * prompt, int threads) {
    atomic_store(&context->abort, 0);
    free(context->text);
    context->text = NULL;

    struct whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = threads > 0 ? threads : 4;
    params.language = (language != NULL && language[0] != '\0') ? language : "auto";
    params.detect_language = false;
    params.translate = false;
    params.no_context = true;
    params.no_timestamps = true;
    params.single_segment = false;
    params.print_special = false;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.suppress_blank = true;
    params.suppress_nst = true;
    params.temperature = 0.0f;
    params.initial_prompt = (prompt != NULL && prompt[0] != '\0') ? prompt : NULL;
    params.abort_callback = should_abort;
    params.abort_callback_user_data = context;

    int result = whisper_full(context->whisper, params, samples, count);
    if (result != 0) {
        return result;
    }

    int segments = whisper_full_n_segments(context->whisper);
    size_t length = 1;
    for (int i = 0; i < segments; i++) {
        length += strlen(whisper_full_get_segment_text(context->whisper, i)) + 1;
    }

    context->text = malloc(length);
    if (context->text == NULL) {
        return -100;
    }

    context->text[0] = '\0';
    for (int i = 0; i < segments; i++) {
        const char * segment = whisper_full_get_segment_text(context->whisper, i);
        while (*segment == ' ') {
            segment++;
        }

        if (*segment == '\0') {
            continue;
        }

        if (context->text[0] != '\0') {
            strcat(context->text, " ");
        }

        strcat(context->text, segment);
    }

    return 0;
}

// UTF-8 text of the last successful cpw_transcribe; valid until the next call or cpw_free.
CPW_API const char * cpw_text(cpw_context * context) {
    return context->text != NULL ? context->text : "";
}

// Stops a running cpw_transcribe from another thread (the recording was stopped).
CPW_API void cpw_abort(cpw_context * context) {
    atomic_store(&context->abort, 1);
}

CPW_API void cpw_free(cpw_context * context) {
    if (context == NULL) {
        return;
    }

    whisper_free(context->whisper);
    free(context->text);
    free(context);
}

// CPU features whisper.cpp was built with (NEON, dot product, FP16), for the log.
CPW_API const char * cpw_system_info(void) {
    return whisper_print_system_info();
}
