package com.akbank.analytics.applications.bo.description;

import java.io.IOException;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/** Planı Excel'de açılabilen CSV olarak yazar (UTF-8 BOM, ';' ayraç — Türkçe Excel için). */
public final class DescriptionReport {

    private DescriptionReport() { }

    public static void write(DescriptionPlan plan, Path file) throws IOException {
        Files.createDirectories(file.getParent());
        try (Writer w = Files.newBufferedWriter(file, StandardCharsets.UTF_8)) {
            w.write('﻿');
            w.write("DURUM;OBJE_YOLU;TIP;DB_YOLU;MEVCUT_ACIKLAMA;YENI_ACIKLAMA\r\n");
            for (DescriptionPlan.Item i : plan.getItems()) {
                w.write(String.join(";",
                        cell(i.getStatus().name()),
                        cell(i.getPath()),
                        cell(i.getTarget() != null ? i.getTarget().getType() : ""),
                        cell(i.getDbPath()),
                        cell(i.getCurrentDescription()),
                        cell(i.getNewDescription())));
                w.write("\r\n");
            }
        }
    }

    private static String cell(String s) {
        if (s == null) return "";
        return "\"" + s.replace("\"", "\"\"") + "\"";
    }
}
