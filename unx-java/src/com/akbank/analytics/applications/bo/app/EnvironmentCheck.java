package com.akbank.analytics.applications.bo.app;

/** Ortam kontrolü: JVM ve her kritik sınıfın hangi jar'dan yüklendiği. */
public class EnvironmentCheck {

    public static void main(String[] args) {
        System.out.println("Java version : " + System.getProperty("java.version"));
        System.out.println("Java vendor  : " + System.getProperty("java.vendor"));
        System.out.println("Java home    : " + System.getProperty("java.home"));
        System.out.println("Mimari       : " + System.getProperty("sun.arch.data.model") + "-bit");
        System.out.println("Library path : " + System.getProperty("java.library.path"));
        System.out.println("Connectivity : " + System.getProperty("businessobjects.connectivity.directory"));
        System.out.println();

        check("com.crystaldecisions.sdk.framework.CrystalEnterprise");
        check("com.sap.sl.sdk.framework.SlContext");
        check("com.sap.sl.sdk.authoring.cms.CmsResourceService");
        check("com.sap.sl.sdk.authoring.local.LocalResourceService");
        check("com.sap.sl.sdk.authoring.businesslayer.RelationalBusinessLayer");
        check("com.sap.sl.sdk.authoring.datafoundation.DataFoundationService");
        check("org.apache.axis.client.Service");
        check("oracle.jdbc.OracleDriver");
    }

    private static void check(String className) {
        try {
            Class<?> c = Class.forName(className);
            String source = (c.getProtectionDomain().getCodeSource() != null)
                    ? c.getProtectionDomain().getCodeSource().getLocation().toString()
                    : "(bilinmiyor)";
            System.out.println("[OK]   " + className + "\n       <- " + source);
        } catch (Throwable t) {
            System.out.println("[HATA] " + className + " : " + t);
        }
    }
}
