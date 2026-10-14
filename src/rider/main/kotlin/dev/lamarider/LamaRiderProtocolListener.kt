package dev.lamarider

import com.intellij.openapi.client.ClientProjectSession
import com.intellij.openapi.components.service
import com.jetbrains.rd.protocol.SolutionExtListener
import com.jetbrains.rd.util.lifetime.Lifetime
import com.jetbrains.rd.ide.model.LamaRiderModel

class LamaRiderProtocolListener : SolutionExtListener<LamaRiderModel> {
    override fun extensionCreated(lifetime: Lifetime, session: ClientProjectSession, model: LamaRiderModel) {
        val service = session.project.service<LamaRiderService>()
        service.attach(lifetime, model)
        service.onCodeVisionClicked = { id -> AspectPopup.show(session.project, id, atMouse = true) }
    }
}
