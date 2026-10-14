package dev.lamarider

import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project

/** Scopes understood by the backend (MetalamaTestSettings). */
enum class TestSettingsScope(val id: String, val label: String) {
    Shared("Shared", "Apply to solution (team-shared)"),
    Personal("Personal", "Apply for me only"),
    Global("Global", "Apply globally"),
}

/** Asks before changing settings: Metalama aspect tests need xUnit Test Runner discovery in Rider. */
object TestSettingsNotification {
    fun show(project: Project) {
        val notification = NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(
            "Metalama aspect tests need Rider settings",
            "This solution uses Metalama.Testing.AspectTesting, but xUnit test discovery is set to Metadata, which does not find aspect tests. " +
                "Switch to Test Runner discovery and stop the orphaned-processes prompt for diff tools opened by failing tests?",
            NotificationType.INFORMATION,
        )
        for (scope in TestSettingsScope.entries) {
            notification.addAction(NotificationAction.createSimpleExpiring(scope.label) {
                project.service<LamaRiderService>().applyTestSettings(scope.id)
            })
        }
        notification.addAction(NotificationAction.createSimpleExpiring("Ignore") {})
        notification.notify(project)
    }
}
