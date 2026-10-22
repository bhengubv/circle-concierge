package com.concierge.wear;

import android.app.Activity;
import android.view.Window;
import java.lang.reflect.Method;
import java.util.concurrent.Executor;
import java.util.function.Consumer;

/** Optional public Wear SDK API. No sensor heuristics or private Binder calls. */
public final class WristGestures {
    private Object manager;
    private Consumer<Object> listener;
    public String status = "Hand gestures unavailable";

    public static WristGestures start(Activity activity, Runnable primary, Runnable dismiss) {
        WristGestures bridge = new WristGestures();
        try {
            Class<?> sdk = Class.forName("com.google.wear.Sdk");
            Class<?> type = Class.forName("com.google.wear.input.GestureInputManager");
            Class<?> event = Class.forName("com.google.wear.input.GestureEvent");
            bridge.manager = sdk.getMethod("getWearManager", android.content.Context.class, Class.class).invoke(null, activity, type);
            int primaryAction = event.getField("ACTION_PRIMARY").getInt(null);
            int dismissAction = event.getField("ACTION_DISMISS").getInt(null);
            int[] supported = (int[]) type.getMethod("getSupportedActions").invoke(bridge.manager);
            if (supported.length == 0) return bridge;
            Method getAction = event.getMethod("getAction");
            bridge.listener = value -> {
                try {
                    int action = (Integer) getAction.invoke(value);
                    android.util.Log.i("ConciergeGestures", "Received action=" + action + " focused=" + activity.hasWindowFocus());
                    if (action == primaryAction) primary.run();
                    else if (action == dismissAction) dismiss.run();
                    type.getMethod("notifyGestureConsumed", String.class, int.class).invoke(bridge.manager, "concierge-wrist", action);
                } catch (Exception e) { android.util.Log.w("ConciergeGestures", "Gesture callback failed", e); }
            };
            Executor executor = command -> activity.runOnUiThread(command);
            type.getMethod("addGestureEventListener", int[].class, Window.class, Executor.class, Consumer.class)
                .invoke(bridge.manager, supported, activity.getWindow(), executor, bridge.listener);
            boolean primaryEnabled = (Boolean) type.getMethod("isActionEnabled", int.class).invoke(bridge.manager, primaryAction);
            boolean dismissEnabled = (Boolean) type.getMethod("isActionEnabled", int.class).invoke(bridge.manager, dismissAction);
            bridge.status = "Double pinch: " + (primaryEnabled ? "on" : "off")
                + " · Wrist turn: " + (dismissEnabled ? "on" : "off");
            android.util.Log.i("ConciergeGestures", bridge.status);
        } catch (Exception e) { android.util.Log.w("ConciergeGestures", "Public Wear gesture API unavailable", e); }
        return bridge;
    }

    public void stop() {
        if (manager == null || listener == null) return;
        try { manager.getClass().getMethod("removeGestureEventListener", Consumer.class).invoke(manager, listener); }
        catch (Exception e) { android.util.Log.w("ConciergeGestures", "Could not remove listener", e); }
        listener = null;
    }
}
