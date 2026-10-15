package dev.lamarider

import com.intellij.ide.BrowserUtil
import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.text.StringUtil
import com.intellij.ui.EditorNotifications
import com.jetbrains.rider.build.BuildHost
import dev.lamarider.actions.AutoBuildAction
import dev.lamarider.explorer.Navigation
import com.jetbrains.rd.util.lifetime.Lifetime
import com.jetbrains.rd.ide.model.LamaRiderModel
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

/** Frontend view of the backend's aspect model: latest parsed model.json, status line, refresh. */
@Service(Service.Level.PROJECT)
class LamaRiderService(val project: Project) {
    private val log = logger<LamaRiderService>()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private var rd: LamaRiderModel? = null

    @Volatile
    var model: ModelData? = null
        private set

    @Volatile
    var status: String = "Waiting for backend…"
        private set

    var onCodeVisionClicked: ((String) -> Unit)? = null

    val debugTransformedCode: Boolean
        get() = rd?.debugTransformedCode?.value ?: false

    fun setDebugTransformedCode(enable: Boolean) {
        rd?.setDebugTransformedCode?.fire(enable)
    }

    /** Rider set up for aspect tests (null: not known yet) and an explanation; see [refreshTestSettingsStatus]. */
    @Volatile
    var testSettingsStatus: Pair<Boolean?, String>? = null
        private set

    fun refreshTestSettingsStatus() {
        rd?.refreshTestSettingsStatus?.fire(Unit)
    }

    fun addLamaDebugConfiguration() {
        rd?.addLamaDebugConfiguration?.fire(Unit)
    }

    fun setAutoBuild(enable: Boolean) {
        rd?.autoBuild?.set(enable)
    }

    fun attach(lifetime: Lifetime, rdModel: LamaRiderModel) {
        rd = rdModel
        rdModel.autoBuild.set(AutoBuildAction.isEnabled())
        // A finished build changes what Metalama sees (compile-time code, references); the backend's own build hook
        // does not fire for Rider's builds in 2026.3.
        var wasBuilding = false
        BuildHost.getInstance(project).building.advise(lifetime) { building ->
            if (wasBuilding && !building) refresh()
            wasBuilding = building
        }
        rdModel.status.advise(lifetime) { status = it ?: ""; fire() }
        rdModel.testSettingsStatus.advise(lifetime) { text ->
            testSettingsStatus = text?.let { rdModel.testSettingsConfigured.value to it }
            fire()
        }
        rdModel.modelPath.advise(lifetime) { path ->
            if (path == null) return@advise
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    model = ModelData.load(path).also(::notifyErrorsIfChanged)
                } catch (e: Exception) {
                    log.warn("Cannot read $path", e)
                    status = "Cannot read model: ${e.message}"
                }
                fire()
            }
        }
        rdModel.codeVisionClicked.advise(lifetime) { id -> onCodeVisionClicked?.invoke(id) }
        rdModel.info.advise(lifetime) { message -> notify("LamaRider", message) }
        rdModel.testSettingsSuggested.advise(lifetime) { TestSettingsNotification.show(project) }
        rdModel.failed.advise(lifetime) { message ->
            NotificationGroupManager.getInstance().getNotificationGroup("LamaRider")
                .createNotification("Metalama aspects could not be refreshed", message, NotificationType.WARNING)
                .notify(project)
        }
    }

    /** Opens an aspect class without source in the solution through ReSharper (SourceLink sources or decompiled). */
    fun navigateToClass(docId: String) {
        rd?.navigateToClass?.fire(docId)
    }

    fun applyTestSettings(scope: String) {
        rd?.applyTestSettings?.fire(scope)
    }

    fun refresh() {
        rd?.refresh?.fire(Unit)
    }

    /** Listener runs on the EDT whenever model or status changes. */
    fun addListener(disposable: Disposable, listener: () -> Unit) {
        listeners += listener
        Disposer.register(disposable) { listeners -= listener }
    }

    fun transformedPathFor(file: String): String? {
        val wanted = File(file).canonicalPath
        return model?.transformedFiles?.firstOrNull { File(it.file).canonicalPath.equals(wanted, ignoreCase = true) }?.transformedPath
    }

    // rd callbacks arrive on a background protocol thread (Rider 2026.3); listeners touch Swing.
    private fun fire() = ApplicationManager.getApplication().invokeLater { listeners.forEach { it() } }

    @Volatile
    private var lastErrorSignature = ""

    /** Metalama errors as a notification, once per new set of errors (not after every build). */
    private fun notifyErrorsIfChanged(model: ModelData) {
        val signature = model.errorSignature()
        if (signature == lastErrorSignature) return
        lastErrorSignature = signature
        EditorNotifications.getInstance(project).updateAllNotifications()
        val errors = model.errors()
        if (errors.isEmpty()) return
        val list = errors.take(3).joinToString("<br>") { e ->
            val where = e.file?.let { " (${File(it).name}${e.line?.let { l -> ":$l" } ?: ""})" } ?: ""
            "<b>${e.id}</b> ${StringUtil.escapeXmlEntities(e.message)}$where"
        } + if (errors.size > 3) "<br>… and ${errors.size - 3} more" else ""
        val notification = NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(
            if (errors.size == 1) "Metalama reported an error" else "Metalama reported ${errors.size} errors",
            list, NotificationType.WARNING,
        )
        val first = errors.first()
        if (first.id.startsWith("LAMA")) {
            notification.addAction(NotificationAction.createSimple("${first.id} documentation") {
                BrowserUtil.browse("https://doc.metalama.net/errors/${first.id.lowercase()}")
            })
        }
        first.file?.let { file ->
            notification.addAction(NotificationAction.createSimple("Go to error") { Navigation.openLine(project, file, (first.line ?: 1) - 1) })
        }
        notification.notify(project)
    }

    fun notify(title: String, content: String, type: NotificationType = NotificationType.INFORMATION) =
        NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(title, content, type).notify(project)
}
