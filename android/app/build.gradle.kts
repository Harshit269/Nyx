import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing comes from android/keystore.properties, which is never committed
// (see .gitignore and README > Releasing). Without it, release builds are left unsigned.
val signing = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "com.amak.phonebridge"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.amak.phonebridge"
        minSdk = 26
        targetSdk = 34
        versionCode = 14
        versionName = "1.4.1"
    }

    signingConfigs {
        if (signing.getProperty("storeFile") != null) {
            create("release") {
                storeFile = rootProject.file(signing.getProperty("storeFile"))
                storePassword = signing.getProperty("storePassword")
                keyAlias = signing.getProperty("keyAlias")
                keyPassword = signing.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    lint {
        abortOnError = true
        warningsAsErrors = false
    }
}

dependencies {
    // unit tests only: the app itself ships with no third-party libraries
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303") // the real org.json, since android.jar only has stubs
}
