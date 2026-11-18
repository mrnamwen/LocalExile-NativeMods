// Spike harness (Linux/Proton): LD_PRELOAD into the Wine process running Game.patched.exe, load a Frida Gum script
// once the PE image is mapped, append everything it send()s to a log, and tell it whenever the inbox file changes.
// Derived from LocalExile's scripts/walkmaps/proton-recorder.cpp. Experimental; not the client host.
//   NATIVEMODS_IMAGE  absolute path of Client/Game.patched.exe
//   NATIVEMODS_AGENT  the script
//   NATIVEMODS_INBOX  a .js file; each time its mtime changes the agent gets {type:'inbox', path}
//   NATIVEMODS_LOG    output log
//   NATIVEMODS_OVERLAY optional folder of files served over the game's (lower-case paths)
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include "frida-gumjs.h"
#include <fcntl.h>
#include <limits.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static FILE *out;

static void on_message(const gchar *message, GBytes *data, gpointer user) {
    (void)data; (void)user;
    if (!out) return;
    fprintf(out, "%s\n", message); fflush(out);
}

static void *run(void *unused) {
    (void)unused;
    const char *image = getenv("NATIVEMODS_IMAGE"), *agent = getenv("NATIVEMODS_AGENT");
    const char *inbox = getenv("NATIVEMODS_INBOX"), *log = getenv("NATIVEMODS_LOG");
    out = fopen(log ? log : "/dev/null", "a");
    uintptr_t base = 0;
    for (int n = 0; n < 600 && !base; n++) {
        FILE *maps = fopen("/proc/self/maps", "r");
        char line[PATH_MAX + 256]; unsigned long start, end, offset; char access[5];
        while (maps && fgets(line, sizeof line, maps)) {
            char *path = strchr(line, '/');
            if (path) path[strcspn(path, "\n")] = 0;
            if (path && !strcmp(path, image) && sscanf(line, "%lx-%lx %4s %lx", &start, &end, access, &offset) == 4 && offset == 0) { base = start; break; }
        }
        if (maps) fclose(maps);
        if (!base) usleep(100000);
    }
    if (!base) { if (out) { fprintf(out, "{\"host\":\"no image mapped\"}\n"); fflush(out); } return NULL; }
    gchar *body = NULL;
    if (!g_file_get_contents(agent, &body, NULL, NULL)) return NULL;
    const char *overlay = getenv("NATIVEMODS_OVERLAY");
    gchar *source = g_strdup_printf("globalThis.HOST={base:'0x%lx',overlay:'%s'};\n%s", (unsigned long)base, overlay ? overlay : "", body);
    g_free(body);
    GMainContext *context = g_main_context_new();
    g_main_context_push_thread_default(context);
    GError *error = NULL;
    GumScript *script = gum_script_backend_create_sync(gum_script_backend_obtain_qjs(), "nativemods-spike", source, NULL, NULL, &error);
    g_free(source);
    if (!script) { if (out) { fprintf(out, "{\"host\":\"script error\",\"message\":\"%s\"}\n", error->message); fflush(out); } return NULL; }
    gum_script_set_message_handler(script, on_message, NULL, NULL);
    gum_script_load_sync(script, NULL);
    if (out) { fprintf(out, "{\"host\":\"loaded\",\"pid\":%ld,\"base\":\"0x%lx\"}\n", (long)getpid(), (unsigned long)base); fflush(out); }
    time_t seen = 0; struct stat info;
    if (inbox && !stat(inbox, &info)) seen = info.st_mtime * 1000000000L + info.st_mtim.tv_nsec;
    for (;;) {
        while (g_main_context_iteration(context, FALSE)) {}
        usleep(50000);
        if (inbox && !stat(inbox, &info)) {
            time_t now = info.st_mtime * 1000000000L + info.st_mtim.tv_nsec;
            if (now != seen) {
                seen = now;
                gchar *message = g_strdup_printf("{\"type\":\"inbox\",\"path\":\"%s\"}", inbox);
                gum_script_post(script, message, NULL);
                g_free(message);
            }
        }
    }
    return NULL;
}

__attribute__((constructor)) static void initialize(void) {
    if (!getenv("NATIVEMODS_IMAGE") || !getenv("NATIVEMODS_AGENT")) return;
    char exe[PATH_MAX]; ssize_t n = readlink("/proc/self/exe", exe, sizeof exe - 1);
    if (n < 0) return;
    exe[n] = 0;
    const char *leaf = strrchr(exe, '/'); leaf = leaf ? leaf + 1 : exe;
    if (!strstr(leaf, "wine")) return;
    FILE *cmd = fopen("/proc/self/cmdline", "r");
    if (!cmd) return;
    char args[16384]; size_t size = fread(args, 1, sizeof args - 1, cmd); fclose(cmd); args[size] = 0;
    int target = 0;
    for (size_t i = 0; i < size; i += strlen(args + i) + 1) {
        const char *name = args + i, *slash = strrchr(name, '/'), *backslash = strrchr(name, '\\');
        if (slash) name = slash + 1;
        if (backslash && backslash >= name) name = backslash + 1;
        size_t length = strlen(name);
        if (length >= 4 && !strcasecmp(name + length - 4, ".exe")) { target = !strcmp(name, "Game.patched.exe"); break; }
    }
    if (!target) return;
    gum_init_embedded();
    (void)gum_process_get_libc_module(); // before Wine's preloader changes the main image metadata
    pthread_t worker;
    if (!pthread_create(&worker, NULL, run, NULL)) pthread_detach(worker);
}
