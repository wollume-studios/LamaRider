package dev.lamarider

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
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

    fun attach(lifetime: Lifetime, rdModel: LamaRiderModel) {
        rd = rdModel
        rdModel.status.advise(lifetime) { status = it ?: ""; fire() }
        rdModel.modelPath.advise(lifetime) { path ->
            if (path == null) return@advise
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    model = ModelData.load(path)
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

    fun notify(title: String, content: String, type: NotificationType = NotificationType.INFORMATION) =
        NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(title, content, type).notify(project)
}
