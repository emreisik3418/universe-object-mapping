package com.akbank.analytics.applications.bo.description;

import com.akbank.analytics.applications.bo.model.Credential;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.ResultSetMetaData;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Açıklamaları Oracle'dan okur (salt okuma).
 * Sorgu bir .sql dosyasındadır ve şu iki kolonu (alias) döndürmelidir:
 *   OBJECT_PATH  : universe içindeki obje yolu, ör. 'Müşteri\Müşteri No' (ayraç / veya \)
 *   DESCRIPTION  : yazılacak açıklama
 * Sondaki ';' ve '/' karakterleri otomatik atılır.
 * Sorgu dosyasında Türkçe karakter (yorum satırları dahil) olmamalı; orai18n.jar yoksa
 * sürücü sorgu metnini DB karakter setine çeviremeyebilir. Veri içindeki Türkçe karakterler sorun değil.
 */
public final class DescriptionSource {

    public static final String PATH_COLUMN = "OBJECT_PATH";
    public static final String DESCRIPTION_COLUMN = "DESCRIPTION";

    private final String jdbcUrl;
    private final Credential credential;

    public DescriptionSource(String jdbcUrl, Credential credential) {
        this.jdbcUrl = jdbcUrl;
        this.credential = credential;
    }

    public static String loadSql(Path file) throws IOException {
        String sql = new String(Files.readAllBytes(file), StandardCharsets.UTF_8);
        if (sql.startsWith("﻿")) sql = sql.substring(1);
        sql = sql.trim();
        while (sql.endsWith(";") || sql.endsWith("/")) {
            sql = sql.substring(0, sql.length() - 1).trim();
        }
        if (sql.isEmpty()) throw new IOException("SQL dosyası boş: " + file.toAbsolutePath());
        return sql;
    }

    public List<DescriptionRow> read(String sql) throws SQLException {
        DriverManager.setLoginTimeout(30);
        try (Connection c = DriverManager.getConnection(jdbcUrl, credential.getUsername(), credential.getPassword())) {
            c.setReadOnly(true);
            try (Statement st = c.createStatement()) {
                st.setQueryTimeout(600);
                // TO_NCHAR: DB karakter seti (ör. WE8ISO8859P9) JDBC sürücüsünde orai18n.jar olmadan
                // okunamıyor; ulusal karakter seti (AL16UTF16) ise her zaman okunabiliyor.
                String wrapped = "SELECT TO_NCHAR(Q." + PATH_COLUMN + ") AS " + PATH_COLUMN +
                                 ", TO_NCHAR(Q." + DESCRIPTION_COLUMN + ") AS " + DESCRIPTION_COLUMN +
                                 " FROM (\n" + sql + "\n) Q";
                try (ResultSet rs = st.executeQuery(wrapped)) {
                    checkColumns(rs.getMetaData());
                    List<DescriptionRow> rows = new ArrayList<>();
                    while (rs.next()) {
                        // Kolon ADIYLA okumak (getString("...")) sürücüde karakter seti çevirisi tetikliyor ve
                        // orai18n.jar olmadan hata veriyor; sırayla okuyoruz (1 = yol, 2 = açıklama).
                        rows.add(new DescriptionRow(rs.getString(1), rs.getString(2)));
                    }
                    return rows;
                }
            }
        }
    }

    private static void checkColumns(ResultSetMetaData md) throws SQLException {
        Set<String> cols = new HashSet<>();
        for (int i = 1; i <= md.getColumnCount(); i++) {
            cols.add(md.getColumnLabel(i).toUpperCase(Locale.ROOT));
        }
        if (!cols.contains(PATH_COLUMN) || !cols.contains(DESCRIPTION_COLUMN)) {
            throw new SQLException("Sorgu " + PATH_COLUMN + " ve " + DESCRIPTION_COLUMN +
                    " kolonlarını (alias) döndürmeli. Gelen kolonlar: " + cols);
        }
    }
}
