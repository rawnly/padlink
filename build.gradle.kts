plugins {
    // AGP 9 compiles Kotlin itself ("built-in Kotlin"); declaring the Kotlin plugin here
    // only pins a newer Kotlin compiler (2.4 = first with Java 26 support).
    id("com.android.application") version "9.2.0" apply false
    id("org.jetbrains.kotlin.android") version "2.4.0" apply false
}
