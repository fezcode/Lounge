// Built by TiviCompatTests from tivi's own watch.c and viconfig.c, so Lounge's file
// formats are checked against the real parser and writer rather than a copy.
//   harness read <path>...   prints position|duration|delay for each path
//   harness config           prints a few config.ini values as tivi sees them
//   harness write <path>     records 123.5 s / 4000 s / -150 ms and saves
//   harness writeconfig      saves a config with known values
#include <stdio.h>
#include <string.h>
#include "watch.h"
#include "viconfig.h"

int main(int argc, char **argv) {
    if (argc < 2) return 2;
    if (!strcmp(argv[1], "read")) {
        watch_load();
        for (int i = 2; i < argc; i++) {
            WatchEntry e;
            if (watch_get(argv[i], &e)) printf("%.3f|%.3f|%d\n", e.position, e.duration, e.sub_delay_ms);
            else printf("missing\n");
        }
        return 0;
    }
    if (!strcmp(argv[1], "config")) {
        ViConfig c;
        viconfig_load(&c);
        printf("pitch_correct=%d\nsub_auto_lang=%s\nsub_color=%06x\nvolume=%.2f\nhw_decode=%d\nsub_font=%s\n",
               c.pitch_correct, c.sub_auto_lang, c.sub_color, c.volume, c.hw_decode, c.sub_font);
        return 0;
    }
    if (!strcmp(argv[1], "write") && argc > 2) {
        WatchEntry e = { 123.5, 4000.0, -150 };
        watch_load();
        watch_put(argv[2], &e);
        watch_save();
        return 0;
    }
    if (!strcmp(argv[1], "writeconfig")) {
        ViConfig c;
        viconfig_defaults(&c);
        c.pitch_correct = false;
        c.sub_color = 0xffe066;
        snprintf(c.sub_auto_lang, sizeof c.sub_auto_lang, "tr");
        c.volume = 0.5f;
        viconfig_save(&c);
        return 0;
    }
    return 2;
}
