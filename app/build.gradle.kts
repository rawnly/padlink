plugins {
    // Kotlin is built into AGP 9: no separate kotlin-android plugin here.
    id("com.android.application")
}

android {
    namespace = "com.padlink"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.padlink"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "0.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug") // sideload-only app
        }
    }

    // Bytecode level for Android; independent of the JDK (26) that runs Gradle.
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

// No dependencies: plain Android framework only.
