package com.akbank.analytics.applications.bo.description;

/**
 * Universe'teki açıklaması güncellenebilen bir obje (dimension, measure, attribute).
 * SDK nesnesini sarmalar; eşleştirme mantığı SDK'dan bağımsız kalsın diye arayüz.
 */
public interface TargetObject {

    /** Business layer içindeki tam yol: "Klasör\Alt Klasör\Obje". */
    String getFullPath();

    /** DIMENSION, MEASURE, ATTRIBUTE... */
    String getType();

    String getDescription();

    void setDescription(String description);
}
