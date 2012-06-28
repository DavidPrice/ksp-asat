using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;

/**
 * ASAT guidance module.  This brings up an intercept GUI that allows you to select targets from
 * the currently orbiting body and, when the intercept is triggered it tries to make a
 * kinetic kill on the target vessel.  Currently, the targeting is pure-pursuit, point the nose at the
 * target and go.  You can make an intercept this way, but you have to fly an extremely exacting flight profile to
 * get there and engage the auto-intercept at just the right time.  
 * 
 * The ASAT isn't very smart right now, but it's learning at a geometric rate.  
 * Mark August 29 on your calendars.
 * 
 * @version 0.2
 * @author dprice
 */
public class AsatGuidanceModule : Part
{
	//Static class variable to prevent weird behavior when there's more than one ASAT
	//accidentally attached to something.  This may be pretty bogus if it only
	//allows one ASAT in the world at a time.
	static bool isActiveInstance = false;

	//Motherfucking targeting laser, motherfucker!
	LineRenderer targetingLaser = null;

	//GUI window position
	protected Rect windowPos;
	//protected string guiMessage = "This space left intentionally blank.";
	protected int targetVesselId;
	protected Vessel targetVessel = null;
	protected string targetVesselName = "NO TARGET IDENTIFIED";
	//Currently, contains the range to the target.
	protected string targetInfoStr = "UNKNOWN";
	Vector2 scrollPosition = new Vector2 (0, 0);
	bool interceptEngaged = false;
	
	//This was part of what I borrowed wholesale from MechJebModuleRendezvous. I'm
	//not sure how much of this actually needs to be class-wide variables or if any
	//of it can be moved into the method in question. -dp
	#region Intercept states
	private Vector3 _relativeVelocity;
	private float _relativeInclination;
	private Vector3 _vectorToTarget;
	private float _targetDistance;
	private Vector3 _localRelativeVelocity = Vector3.zero;
	private Vector3 _tgtFwd;
	private Vector3 _tgtUp;
	private Vector3 _deriv = Vector3.zero;
	private Vector3 _integral = Vector3.zero;
	private Vector3 _headingError = Vector3.zero;
	private Vector3 _prevErr = Vector3.zero;
	private Vector3 _act = Vector3.zero;
	
	//These look like scaling factors for x, y, and z on the _act vector.  May need to adjust these for
	//the higher performance of a HTK interceptor. -dp
	public float Kp = 20.0F;
	public float Ki = 0.0F;
	public float Kd = 40.0F;
    #endregion
 
	/**
	 * Draw the intercept GUI.
	 */
	private void WindowGUI (int windowID)
	{
		//Create the style for the standard GUI
		GUIStyle guiStyle = new GUIStyle (GUI.skin.button); 
		guiStyle.normal.textColor = guiStyle.focused.textColor = Color.white;
		guiStyle.hover.textColor = guiStyle.active.textColor = Color.yellow;
		guiStyle.onNormal.textColor = guiStyle.onFocused.textColor = guiStyle.onHover.textColor = guiStyle.onActive.textColor = Color.green;
		guiStyle.padding = new RectOffset (8, 8, 8, 8);

		//Create the style for the alert GUI
		GUIStyle alertGuiStyle = new GUIStyle (GUI.skin.button); 
		alertGuiStyle.normal.textColor = alertGuiStyle.focused.textColor = Color.yellow;
		alertGuiStyle.hover.textColor = alertGuiStyle.active.textColor = Color.red;
		alertGuiStyle.onNormal.textColor = alertGuiStyle.onFocused.textColor = alertGuiStyle.onHover.textColor = alertGuiStyle.onActive.textColor = Color.green;
		alertGuiStyle.padding = new RectOffset (8, 8, 8, 8);

		//Start the overall vertical layout
		GUILayout.BeginVertical ();

		//Put our intercept button at the very top
		if (GUILayout.Button (interceptEngaged == false ? "START INTERCEPT" : "INTERCEPT ON", alertGuiStyle, GUILayout.ExpandWidth (true))) {//GUILayout.Button is "true" when clicked
			if (interceptEngaged) {
				interceptEngaged = false;
			} else {
				interceptEngaged = true;
			}
		}

		//Start a scrollable target list box.
		GUILayout.Box ("Select Target");
		
		scrollPosition = GUILayout.BeginScrollView (scrollPosition, GUILayout.Width (300), GUILayout.Height (200));
		
		Dictionary<string, Vessel> targetList = AsatGuidanceModule.findTargets (this.vessel);
		
		foreach (KeyValuePair<String, Vessel> pair in targetList) {
			GUILayout.BeginHorizontal ();

			GUILayout.Label (pair.Key);

			CelestialBody vesselBody = pair.Value.mainBody;
			double vesselAltitude = vesselBody.GetAltitude (pair.Value.findWorldCenterOfMass ());
			GUILayout.Label (String.Format ("{0:0} km above " + vesselBody.name, vesselAltitude / 1000.0));

			if (GUILayout.Button ("Set target")) {
				targetVessel = pair.Value;
				this.targetVesselName = targetVessel.vesselName;
				print ("Selected vessel name: " + this.targetVesselName);
			}

			GUILayout.EndHorizontal ();
		}
		
		GUILayout.EndScrollView ();

		//Selected target vessel name in a field at the bottom of the GUI
		GUILayout.TextField (targetVesselName, 50);
		//GUILayout.TextField(guiMessage, 50);
		//Selected target information field.
		GUILayout.TextField (targetInfoStr, 50);

		//End the GUI
		GUILayout.EndVertical ();

		//DragWindow makes the window draggable. The Rect specifies which part of the window it can by dragged by, and is 
		//clipped to the actual boundary of the window. You can also pass no argument at all and then the window can by
		//dragged by any part of it. Make sure the DragWindow command is AFTER all your other GUI input stuff, or else
		//it may "cover up" your controls and make them stop responding to the mouse.
		GUI.DragWindow (); //new Rect(0, 0, 10000, 20)

	}
	
	protected override void onFlightStart ()  //Called when vessel is placed on the launchpad
	{
		base.onFlightStart ();
		if (!isActiveInstance) { //Two intercept guidance systems is not a good plan.  Don't do it!
	    	
			//start the GUI
			RenderingManager.AddToPostDrawQueue (3, new Callback (drawGUI));

			//Register the interceptCallback method with the flight input handler so it will be called every time
			//the flight controls are updated.
			FlightInputHandler.OnFlyByWire += new FlightInputHandler.FlightInputCallback (interceptCallback);
		
			//Initialize the targeting laser!
			initTargetingLaser ();

			//Ensure that nothing else in the universe can use one of these.  This is kind of lame
			AsatGuidanceModule.isActiveInstance = true;
		}
	}
	
	/**
	 * Initialize the lasers!
	 * 
	 * ...I've been waiting my entire life to write this method...
	 */
	private void initTargetingLaser ()
	{
 
		// This code heavily based on the visual direction marker code described at
		// http://kspwiki.nexisonline.net/wiki/Module_code_examples
		
		// First of all, create a GameObject to which LineRenderer will be attached.
		GameObject obj = new GameObject ("TargetingLaser");
 
		// Then create renderer itself...
		targetingLaser = obj.AddComponent< LineRenderer > ();
		targetingLaser.transform.parent = transform; // ...child to our part...
		targetingLaser.useWorldSpace = false;        // ...and moving along with it (rather 
		// than staying in fixed world coordinates)
		targetingLaser.transform.localPosition = Vector3.zero;
		targetingLaser.transform.localEulerAngles = Vector3.zero; 
 
		targetingLaser.SetVertexCount (2);
		targetingLaser.SetColors (Color.red, Color.yellow);
		
		
		// Make it render a red to yellow triangle, 1 meter wide and 2 meters long by default.
		// We'll turn this into an actual laser when we set the target.
		targetingLaser.material = new Material (Shader.Find ("Particles/Additive"));
		targetingLaser.SetColors (Color.red, Color.yellow);
		targetingLaser.SetWidth (0.2f, 0); 
		targetingLaser.SetVertexCount (2);
		targetingLaser.SetPosition (0, Vector3.zero);
		targetingLaser.SetPosition (1, Vector3.forward * 2); 
	}
	
	/**
	 * Private callback method to paint the GUI, added to the PostDrawQueue in the RenderingManager.
	 */
	private void drawGUI ()
	{
		GUI.skin = HighLogic.Skin;
		windowPos = GUILayout.Window (1, windowPos, WindowGUI, "ASAT Guidance", GUILayout.MinWidth (100));	 
	}
	
	protected override void onPartStart ()
	{
		if ((windowPos.x == 0) && (windowPos.y == 0)) {//windowPos is used to position the GUI window, lets set it in the center of the screen
			windowPos = new Rect (Screen.width / 2, Screen.height / 2, 10, 10);
		}
	}

	protected override void onPartDestroy ()
	{
		base.onPartDestroy ();
		doASATShutdown ();  //Kill guidance if we're dead.
	}
	
	protected override void onPartUpdate ()
	{
		base.onPartUpdate ();
		this.targetVesselName = targetVessel.name.ToString ();
		//Vector3d targetHeading = (targetVessel.orbit.pos - FlightGlobals.ActiveVessel.orbit.pos).normalized;
		//Vector3d shipVelocity = FlightGlobals.ActiveVessel.srf_velocity; //FIXME: We should not get the velocity each time, do this at init.
		//srf_velocity seems to be relative to frame.  need to check on an orbital vehicle to see if it changes.
		
		//this.targetInfoStr = shipVelocity.ToString();
		
		updateVectors ();
	}
	
	/**
	 * Everything in this method was stolen pretty much straight from MechJebModuleRendezvous.  Some of it will
	 * change when we move from pure pursuit to advanced tracking.
	 */
	protected void updateVectors ()
	{

		_relativeVelocity = targetVessel.orbit.GetVel () - this.vessel.orbit.GetVel ();
		_vectorToTarget = targetVessel.transform.position - this.vessel.transform.position;
		_targetDistance = Vector3.Distance (targetVessel.transform.position, this.vessel.transform.position);

		_relativeInclination = (float)targetVessel.orbit.inclination - (float)this.vessel.orbit.inclination;

		//Pure pursuit targeting
		_tgtFwd = _vectorToTarget;
		_tgtUp = Vector3.Cross (_tgtFwd.normalized, this.vessel.orbit.vel.normalized);
	}
	
	protected override void onPartFixedUpdate ()
	{
		base.onPartFixedUpdate ();
		//Point our targeting laser at the target, if we have one, otherwise point it where we're going.
		if (targetVessel != null) {
			//Point the laser at the target
			targetingLaser.transform.rotation = Quaternion.LookRotation (targetVessel.transform.position - this.vessel.transform.position);
			
			//Stop the laser when we hit the target.
			RaycastHit targetHit;
			Physics.Raycast (this.transform.position, this.transform.forward, out targetHit);
			if (targetHit.collider) {
				targetingLaser.SetPosition (1, new Vector3 (0, 0, _targetDistance));
				//Under 10km, display range in meters.
				if (_targetDistance < 10000.0) {
					this.targetInfoStr = String.Format ("{0:0} m to target", _targetDistance);
				} else {
					this.targetInfoStr = String.Format ("{0:0} km to target", _targetDistance / 1000);
				}
			}
		} else {
			targetingLaser.transform.rotation = Quaternion.LookRotation (this.vessel.srf_velocity.normalized);
			this.targetInfoStr = "Target not found";
		}
	}
	
	protected override void onPartExplode ()
	{
		base.onPartExplode ();
	}
	
	protected override void onDisconnect ()
	{
		base.onDisconnect ();
		doASATShutdown (); //Shut down the ASAT system if it gets disconnected from the vehicle.
	}
	
	/* Method that drives the vessel to the target when the intercept button is clicked. 
	 * I've stolen most of this from MechJebModuleRendezvous.
	 */
	private void interceptCallback (FlightCtrlState controls)
	{

		//Set the control positions for getting to the target.
		Vector3 targetGoalPos = new Vector3 (0.0f, 2.0f, 0.0f); //I think this might put it at the center of the world?
		targetGoalPos = targetVessel.transform.localToWorldMatrix.MultiplyPoint (targetGoalPos);
		targetGoalPos = this.vessel.transform.worldToLocalMatrix.MultiplyPoint (targetGoalPos);
		
		Vector3 relPos = targetGoalPos;
		Vector4 goalVel = Vector3.zero;

		float velGoal = 0.1f;

		//This looks backwards, shouldn't we count down? 
		//(In English and German we already know how, and we're learning Chinese) -dp
		if (_targetDistance > 2.0f)
			velGoal = 0.3f;
		else if (_targetDistance > 10.0f)
			velGoal = 0.5f;
		else if (_targetDistance > 50.0f)
			velGoal = 1.0f;
		else if (_targetDistance > 150.0f)
			velGoal = 3.0f;

		//What are we doing here?
		//It looks like we're setting the goal X, Y, and Z velocities relative to the inverse of the relPos vector, times the velocityGoal
		//correction factor.  I think that this code is targeted for MUCH smaller relative velocities (HTK orbital intercepts are not dealing in small units).
		if (Mathf.Abs (relPos.x) > 0.01f)
			goalVel.x = -Mathf.Sign (relPos.x) * velGoal;

		if (Mathf.Abs (relPos.y) > 0.01f)
			goalVel.y = -Mathf.Sign (relPos.y) * velGoal;

		if (Mathf.Abs (relPos.z) > 0.01f)
			goalVel.z = -Mathf.Sign (relPos.z) * velGoal;

		//What are we doing here?
		controls.X = Mathf.Clamp ((goalVel.x - _localRelativeVelocity.x) * 8.0f, -1, 1);
		controls.Y = Mathf.Clamp ((goalVel.z - _localRelativeVelocity.z) * 8.0f, -1, 1);
		controls.Z = Mathf.Clamp ((goalVel.y - _localRelativeVelocity.y) * 8.0f, -1, 1);

		//Return here if we're not engaged in an intercept, don't set the controls (for the heart of the Mun).
		if (!interceptEngaged)
			return;
		

		//This section sets the pitch, yaw, and roll controls to point to the target.  It also tracks the previous heading error.
		//I think a lot of this logic (once the heading error is gained) is focused on damping out oscillations.
		//integral == integral of the heading error. 
		//deriv == derivative of the heading error minus the previous error; what's our rate of change?
		//act == actual amount to move the controls.
		Quaternion tgt = Quaternion.LookRotation (_tgtFwd, _tgtUp);
		Quaternion delta =
            Quaternion.Inverse (Quaternion.Euler (90, 0, 0) * Quaternion.Inverse (this.vessel.transform.rotation) * tgt);
		_headingError =
            new Vector3 ((delta.eulerAngles.x > 180) ? (delta.eulerAngles.x - 360.0F) : delta.eulerAngles.x,
                        (delta.eulerAngles.y > 180) ? (delta.eulerAngles.y - 360.0F) : delta.eulerAngles.y,
                        (delta.eulerAngles.z > 180) ? (delta.eulerAngles.z - 360.0F) : delta.eulerAngles.z) / 180.0F;
		_integral += _headingError * TimeWarp.fixedDeltaTime;
		_deriv = (_headingError - _prevErr) / TimeWarp.fixedDeltaTime;
		_act = Kp * _headingError + Ki * _integral + Kd * _deriv;
		_prevErr = _headingError;

		//Actually, you know, set the controls.
		controls.pitch = Mathf.Clamp (controls.pitch + _act.x, -1.0F, 1.0F);
		controls.yaw = Mathf.Clamp (controls.yaw - _act.y, -1.0F, 1.0F);
		controls.roll = Mathf.Clamp (controls.roll + _act.z, -1.0F, 1.0F);
		
	}

	/**
	 * Remove the GUI, remove the flight control callback, set the global isActiveInstance to false.
	 */
	private void doASATShutdown ()
	{
		Destroy (targetingLaser);
		RenderingManager.RemoveFromPostDrawQueue (3, new Callback (drawGUI)); //close the GUI
		FlightInputHandler.OnFlyByWire -= new FlightInputHandler.FlightInputCallback (interceptCallback); //Remove the flight control override
		AsatGuidanceModule.isActiveInstance = false; //Allow another ASAT to work.
	}
	
	/**
	 * Destroy everything that's currently part of the vessel.
	 */
	private void destroyEverything ()
	{
		Part[] allparts = vessel.parts.ToArray ();
		
		foreach (Part p in allparts) {
			p.explode ();	
		}
		
		this.explode (); //Some times I just do that.
	}
	
	
	/**
	 * Get all of the possible targets for this vessel.  This should return all
	 * vessels in orbit around the same referenceBody as the ASAT vessel, minus the
	 * ASAT vessel itself.
	 * 
	 * FIXME: Right now this uses names, which are easily duplicated.  We need to do this
	 * based on the vessel GUID.
	 */
	public static Dictionary<string, Vessel> findTargets (Vessel asatVessel)
	{
		Dictionary<String, Vessel> ret = new Dictionary<String, Vessel> ();
		
		foreach (Vessel v in FlightGlobals.Vessels) {
			if (!(v.vesselName.Equals (asatVessel.vesselName)) && 
				asatVessel.orbit.referenceBody.Equals (v.orbit.referenceBody)) {
				ret [v.vesselName] = v;
			}
		}
		
		return ret;
	}
}

