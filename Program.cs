using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        List<IMyThrust> thrusters = new List<IMyThrust>();
        List<IMyShipConnector> connectors = new List<IMyShipConnector>();
        List<IMyGasTank> tanks = new List<IMyGasTank>();
        List<IMyFlightMovementBlock> flightMovementBlocks = new List<IMyFlightMovementBlock>();
        List<IMyBasicMissionBlock> basicMissionBlocks = new List<IMyBasicMissionBlock>();
        List<IMyOffensiveCombatBlock> offensiveCombatBlocks = new List<IMyOffensiveCombatBlock>();
        List<IMyRadioAntenna> antennas = new List<IMyRadioAntenna>();

        IMyBroadcastListener _myBroadcastListener;
        IMyOffensiveCombatBlock droneOffensiveBlock = null;
        IMyShipController droneShipController = null;
        IMyShipConnector carrier_connector = null;
        IMyShipConnector droneConnector = null;
        IMyProgrammableBlock droneControlProgrammableBlock = null;
        IMyProgrammableBlock spugProgrammableBlock = null;
        IMyRemoteControl myRemoteControl;
        IMyRadioAntenna droneAntenna;
        IMyTextPanel lcdPanel;

        Vector3D Start = new Vector3D(0, 0, 0);
        Vector3D dockWaypoint = new Vector3D(0, 0, 0);
        Vector3D carrierForwardDir = new Vector3D(0, 0, 0);
        Vector3D carrierCenterPosition = new Vector3D(0, 0, 0);

        MyIni _ini = new MyIni();
        DebugAPI Debug;

        bool droneLaunchFinished = false;
        bool droneLaunching = false;
        bool droneLaunched = true;
        bool droneDocking = false;
        bool droneDocked = false;
        bool droneIsFlyingToDockingWaypoint = false;
        bool droneIsForwardOfCenter = false;
        bool updateAntennaWithLaunching = false;
        bool updateAntennaWithDocking = false;
        bool updateAntennaWithScanning = false;
        bool updateAntennaWithAttacking = false;
        bool updateAntennaWithDocked = false;
        bool droneDisableAntennaOnDocking = false;

        float droneUpwardOverrideThrust = 150000.0f;
        float droneThrustValue = 100.0f;
        float droneLaunchDistance = 150.0f;
        float droneThrustMultiplier = 10000;
        float dockingConnectorWaypointOffset = -70.0f;
        float remoteControlDockingSpeedLimit = 100.0f;

        string _thrust = "100";
        string _launchDistance = "100";
        string _disableAntennaOnDock = "true";
        string _broadCastTag = "DRONE_CONTROL";

        const string INI_SECTION_GENERAL = "Drone Control";
        const string INI_GENERAL_THRUST = "Thrust";
        const string INI_GENERAL_LAUNCH_DISTANCE = "Distance";
        const string INI_GENERAL_DISABLE_ANTENNA_ON_DOCK = "Disable Antenna on Dock";

        const string INI_COMMENT_THRUST = @"How much thrust the ship should use when moving in this direction.";
        const string INI_COMMENT_LAUNCH_DISTANCE = @"How far the ship should move during launch.";
        const string INI_COMMENT_DISABLE_ANTENNA_ON_DOCK = @"Should the Grid disable its Antenna when docked?";

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10;

            Debug = new DebugAPI(this);

            if (!IsCarrierGrid())
                UpdateCustomData();

            // Update all the blocks from the grid in the lists
            UpdateGridBlocks();

            if (!IsCarrierGrid())
                RenameGridBasedOnCarrierConnectorAndSuffixBlocks();

            _myBroadcastListener = IGC.RegisterBroadcastListener(_broadCastTag);
            _myBroadcastListener.SetMessageCallback(_broadCastTag);
        }

        public void Save()
        {
            // Called when the program needs to save its state. Use
            // this method to save your state to the Storage field
            // or some other means. 
            // 
            // This method is optional and can be removed if not
            // needed.
        }

        public void Main(string argument, UpdateType updateType)
        {
            if (ShouldRunMain(argument, updateType))
            {
                HandleTerminalCommands(argument);
            }

            if ((updateType & UpdateType.IGC) > 0 && _myBroadcastListener != null)
            {
                UpdateGridBlocks();

                while (_myBroadcastListener.HasPendingMessage)
                {
                    var myIGCMessage = _myBroadcastListener.AcceptMessage();
                    if (myIGCMessage.Tag == _broadCastTag)
                    {
                        argument = myIGCMessage.Data.ToString();
                        HandleIGCCommunication(argument);
                    }
                }
            }

            if ((updateType & UpdateType.Update1) != 0)
            {
                ExecuteUpdate1Loops();
            }

            if ((updateType & UpdateType.Update10) != 0)
            {
                ExecuteUpdate10Loops();
            }
        }

        private void ExecuteUpdate1Loops()
        {
            if (droneLaunching)
                CheckIfDroneHasFinishedLaunch();

            if (droneIsFlyingToDockingWaypoint)
            {
                CheckIfDroneHasArrivedAtDockingWaypoint();
                Debug.DrawLine(myRemoteControl.GetPosition(), dockWaypoint, Color.Yellow, 0.1f, 0.1f, true);
            }
        }

        private void ExecuteUpdate10Loops()
        {
            if (!droneLaunching && !droneDocking && !droneDocked)
                UpdateAntennaWithOffensiveBlockCondition();
            else if (droneDocked && !droneLaunched)
                updateAntennaWithDocked = true;

            CheckConditionsAndUpdateAntenna();
        }

        private void HandleTerminalCommands(string argument)
        {
            UpdateGridBlocks();

            if (IsCarrierGrid())
            {
                if (argument == "recall")
                {
                    IGC.SendBroadcastMessage(_broadCastTag, "recall");
                    Echo("Sending recall request");
                }

                if (argument == "carrier_launch")
                {
                    Echo("Sending IGC - Launch Carrier Drones");
                    IGC.SendBroadcastMessage(_broadCastTag, "carrier_launch");
                }
            }
            else
            {
                if (argument == "dock")
                {
                    if (carrier_connector != null)
                        Dock();
                    else
                        Echo("No Carrier Connector set!");
                }

                if (argument == "drone_launch")
                    LaunchDrone();

                if (argument == "disable_ai")
                    DisableAIBehaviours();

                if (argument == "docked")
                    Docked();

                if (argument == "control")
                    ControlDrone();
            }
        }

        private void HandleIGCCommunication(string argument)
        {
            if (IsCarrierGrid())
            {
                // Handle Carrier Communication
            }
            else
            {
                if (carrier_connector != null && droneConnector != null && droneConnector.Status != MyShipConnectorStatus.Connected)
                {
                    if (argument == "recall")
                    {
                        Dock();
                    }
                }

                if (argument == "carrier_launch")
                {
                    Echo("Drone: Received IGC Carrier Launch command");
                    LaunchDrone();
                }
            }
        }

        void CheckIfDroneHasFinishedLaunch()
        {
            if (Vector3D.Distance(Start, Me.GetPosition()) >= droneLaunchDistance && droneLaunching && !droneLaunchFinished && !droneIsFlyingToDockingWaypoint)
            {
                lcdPanel.WriteText("Launch Finished\n", true);
                FinishLaunch();
            }
            else
            {
                // if (!droneLaunchFinished && droneLaunching && !droneIsFlyingToDockingWaypoint)
                // {
                //     Debug.DrawLine(carrier_connector.GetPosition(), carrierCenterPosition, Color.Yellow, 0.1f, 0.1f, true);
                //     if (droneIsForwardOfCenter)
                //     {
                //         double distanceToLaunchStop = Vector3D.Distance(Start, Me.GetPosition());
                //         lcdPanel.WriteText("", false);
                //         lcdPanel.WriteText($"Launching Forward\nLaunch Distance:\n {distanceToLaunchStop:F2}\n", false);
                //     }
                //     else
                //     {
                //         double distanceToLaunchStop = Vector3D.Distance(Start, Me.GetPosition());
                //         lcdPanel.WriteText("", false);
                //         lcdPanel.WriteText($"Launching Backward\nLaunch Distance:\n {distanceToLaunchStop:F2}\n", false);
                //     }
                // }
            }
        }

        void Dock()
        {
            ToggleThrusterOverride(false);
            DisableAIBehaviours();
            SetRemoteControlWaypointFromMessage();
            droneIsFlyingToDockingWaypoint = true;
            droneDocking = true;
            droneLaunched = false;
            updateAntennaWithDocking = true;
        }

        void SetRemoteControlWaypointFromMessage()
        {
            if (carrier_connector == null)
            {
                Echo("Carrier connector not found");
                return;
            }

            var offsetLocal = new Vector3D(0, 0, dockingConnectorWaypointOffset);
            var finalDestination = Vector3D.Transform(offsetLocal, carrier_connector.WorldMatrix);
            dockWaypoint = finalDestination;

            myRemoteControl.ClearWaypoints();
            myRemoteControl.AddWaypoint(finalDestination, "Carrier Connector Position");
            myRemoteControl.FlightMode = FlightMode.OneWay;
            myRemoteControl.SpeedLimit = remoteControlDockingSpeedLimit;
            myRemoteControl.ApplyAction("CollisionAvoidance_On");
            myRemoteControl.ApplyAction("AutoPilot_On");
            //lcdPanel.WriteText($"GPS:{Me.CubeGrid.CustomName}:{finalDestination.X}:{finalDestination.Y}:{finalDestination.Z}:#FF75C9F1", false);
            //Echo($"Flying to GPS coordinates: {finalDestination}");
        }

        void UpdateCustomData(bool writeOnly = false)
        {
            _ini.Clear();
            if (_ini.TryParse(Me.CustomData) && !writeOnly)
            {
                _thrust = _ini.Get(INI_SECTION_GENERAL, INI_GENERAL_THRUST).ToString(_thrust);
                _launchDistance = _ini.Get(INI_SECTION_GENERAL, INI_GENERAL_LAUNCH_DISTANCE).ToString(_launchDistance);
                _disableAntennaOnDock = _ini.Get(INI_SECTION_GENERAL, INI_GENERAL_DISABLE_ANTENNA_ON_DOCK).ToString(_disableAntennaOnDock);
            }

            _ini.Set(INI_SECTION_GENERAL, INI_GENERAL_THRUST, _thrust);
            _ini.Set(INI_SECTION_GENERAL, INI_GENERAL_LAUNCH_DISTANCE, _launchDistance);
            _ini.Set(INI_SECTION_GENERAL, INI_GENERAL_DISABLE_ANTENNA_ON_DOCK, _disableAntennaOnDock);

            _ini.SetComment(INI_SECTION_GENERAL, INI_GENERAL_THRUST, INI_COMMENT_THRUST);
            _ini.SetComment(INI_SECTION_GENERAL, INI_GENERAL_LAUNCH_DISTANCE, INI_COMMENT_LAUNCH_DISTANCE);
            _ini.SetComment(INI_SECTION_GENERAL, INI_GENERAL_DISABLE_ANTENNA_ON_DOCK, INI_COMMENT_DISABLE_ANTENNA_ON_DOCK);

            string output = _ini.ToString();
            if (output != Me.CustomData)
            {
                Me.CustomData = output;
            }
            Echo("Updated Custom Data");
        }

        void LaunchDrone()
        {
            //lcdPanel.WriteText("Launch Method Started\n", true);
            droneLaunching = true;
            droneLaunchFinished = false;
            droneDocking = false;
            droneDocked = false;
            Start = Me.GetPosition();
            updateAntennaWithLaunching = true;

            SaveCarrierConnector();
            ToggleHydrogenThrusters(true);
            ToggleHydrogenTanks(true);
            ToggleHydrogenTankStockpile(false);
            ToggleAntennas(true);

            if (spugProgrammableBlock != null)
            {
                if (carrier_connector != null)
                    spugProgrammableBlock.TryRun(carrier_connector.CustomName); //Updating SPUG with Connector name
                else
                    Echo("No Carrier Connector set!");
            }
            else
            {
                lcdPanel.WriteText("SPUG PB not found\n", true);
            }

            // Retrieve custom data from the programming block
            string customData = Me.CustomData;

            // Parse the custom data to determine the direction and thrust value
            string[] dataLines = customData.Split('\n');

            foreach (string line in dataLines)
            {
                // Split each line using '=' to separate key and value
                string[] parts = line.Split('=');

                if (parts.Length == 2)
                {
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    // Check for comments and skip them
                    if (key.StartsWith(";"))
                        continue;

                    if (key.ToLower() == "thrust")
                        float.TryParse(value, out droneThrustValue);
                    else if (key.ToLower() == "distance")
                        float.TryParse(value, out droneLaunchDistance);
                    else if (key.ToLower() == "disable antenna on dock")
                        bool.TryParse(value, out droneDisableAntennaOnDocking);
                }
            }

            carrierCenterPosition = carrier_connector.CubeGrid.WorldAABB.Center;
            carrierForwardDir = new Vector3D(0, 0, 0);
            List<IMyShipController> shipController = new List<IMyShipController>();
            GridTerminalSystem.GetBlocksOfType(shipController);
            foreach (var controller in shipController)
            {
                if (controller.CubeGrid == carrier_connector.CubeGrid)
                {
                    //Echo($"Found Carrier Controller: {controller.CustomName}");
                    carrierForwardDir = controller.WorldMatrix.Forward;
                }
            }
            droneIsForwardOfCenter = IsDroneForwardOfCarrierCenter(carrier_connector.GetPosition(), carrierCenterPosition, carrierForwardDir);

            if (droneIsForwardOfCenter)
            {
                ToggleThrusterOverride(true, "forward", (droneThrustValue * droneThrustMultiplier));
            }
            else
            {
                ToggleThrusterOverride(true, "backward", (droneThrustValue * droneThrustMultiplier));
            }

            ToggleBasicTaskBehaviour(false);
            ToggleOffensiveBlockkBehaviour(false);
            ToggleAIMoveBehaviour(false);
            ToggleConnectorLock(false);
        }

        void FinishLaunch()
        {
            ToggleThrusterOverride(false);
            ToggleBasicTaskBehaviour(true);
            ToggleOffensiveBlockkBehaviour(true);
            ToggleAIMoveBehaviour(true);
            ToggleMoveBlockCollisionAvoidance(true);
            updateAntennaWithScanning = true;

            droneLaunching = false;
            droneLaunchFinished = true;
            droneLaunched = true;
            //Runtime.UpdateFrequency = UpdateFrequency.None;
            //lcdPanel.WriteText("Launch Method Finished\n", true);
        }

        void Docked()
        {
            ToggleHydrogenThrusters(false);
            ToggleHydrogenTanks(true);
            ToggleHydrogenTankStockpile(true);
            if (droneDisableAntennaOnDocking)
                ToggleAntennas(false);
            
            droneDocking = false;
            droneDocked = true;
            droneLaunched = false;
            updateAntennaWithDocked = true;
        }

        void ControlDrone()
        {
            ToggleThrusterOverride(false);
            ToggleBasicTaskBehaviour(false);
            ToggleOffensiveBlockkBehaviour(false);
            ToggleAIMoveBehaviour(false);
            ToggleHydrogenThrusters(true);
            ToggleHydrogenTanks(true);
            ToggleHydrogenTankStockpile(false);

            lcdPanel.WriteText("Controlling\n", true);
            //Echo("Controlling");
        }

        void DisableAIBehaviours()
        {
            ToggleBasicTaskBehaviour(false);
            ToggleOffensiveBlockkBehaviour(false);
            ToggleAIMoveBehaviour(false);

        }

        void UpdateGridBlocks()
        {
            GridTerminalSystem.GetBlocksOfType(connectors);
            GridTerminalSystem.GetBlocksOfType(thrusters);
            GridTerminalSystem.GetBlocksOfType(tanks);
            GridTerminalSystem.GetBlocksOfType(flightMovementBlocks);
            GridTerminalSystem.GetBlocksOfType(basicMissionBlocks);
            GridTerminalSystem.GetBlocksOfType(offensiveCombatBlocks);
            GridTerminalSystem.GetBlocksOfType(antennas);
            FindAndStoreDroneShipController();
            FindAndStoreDroneControlProgrammableBlock();
            FindAndStoreDroneSpugProgrammableBlock();
            FindAndStoreDroneRemoteControl();
            FindAndStoreLCDPanel();
            FindAndStoreDroneConnector();
            FindAndStoreDroneAntenna();
            FindAndStoreDroneOffensiveBlock();
        }

        void ToggleThrusterOverride(bool enable, string direction = "", float thrustValue = 0.0f)
        {
            var forward = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Backward);
            var backward = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Forward);
            var up = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Down);
            var down = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Up);
            var left = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Right);
            var right = droneShipController.Orientation.TransformDirection(Base6Directions.Direction.Left);

            List<IMyThrust> droneThrusters = new List<IMyThrust>();
            GridTerminalSystem.GetBlocksOfType(droneThrusters);

            foreach (IMyThrust thruster in droneThrusters)
            {
                if (thruster.CubeGrid == Me.CubeGrid)
                {
                    MyBlockOrientation thrusterDirection = thruster.Orientation;
                    if (enable)
                    {
                        if (direction == "forward" && thrusterDirection.Forward == forward)
                            thruster.SetValue("Override", thrustValue);
                        else if (direction == "backward" && thrusterDirection.Forward == backward)
                            thruster.SetValue("Override", thrustValue);
                        else if (thrusterDirection.Forward == up)
                            thruster.SetValue("Override", droneUpwardOverrideThrust);
                    }
                    else
                    {
                        thruster.SetValue("Override", 0.0f);
                    }
                }
            }
        }

        void ToggleConnectorLock(bool connect)
        {
            // Get all Connectors on the current grid
            // Toggle the state of each Connector
            foreach (var connector in connectors)
            {
                if (connector.CubeGrid == Me.CubeGrid)
                {
                    if (connect)
                    {
                        connector.Connect();
                        //Echo($"{connector.CustomName} connected.");
                    }
                    else
                    {
                        connector.Disconnect();
                        //Echo($"{connector.CustomName} disconnected.");
                    }
                }
            }
        }

        void ToggleHydrogenThrusters(bool enable)
        {
            // Toggle the state of each Hydrogen Thruster
            foreach (var thruster in thrusters)
            {
                if (thruster.CubeGrid == Me.CubeGrid && thruster.BlockDefinition.SubtypeId.Contains("Hydrogen"))
                {
                    if (enable)
                        thruster.Enabled = enable;
                    else
                        thruster.Enabled = enable;
                }
            }
            //Echo($"Hydrogren Thrusters Enable = {enable}.");
        }

        void ToggleHydrogenTanks(bool enable)
        {
            foreach (var tank in tanks)
            {
                if (tank.CubeGrid == Me.CubeGrid && tank.BlockDefinition.SubtypeId.Contains("Hydrogen"))
                {
                    if (enable)
                        tank.Enabled = enable;
                    else
                        tank.Enabled = enable;
                }
            }
            //Echo($"Hydrogren Tanks Enable = {enable}.");
        }

        void ToggleHydrogenTankStockpile(bool enable)
        {
            foreach (var tank in tanks)
            {
                if (tank.CubeGrid == Me.CubeGrid && tank.BlockDefinition.SubtypeId.Contains("Hydrogen"))
                {
                    if (enable)
                        tank.Stockpile = enable;
                    else
                        tank.Stockpile = enable;
                }
            }
            //Echo($"Hydrogren Stockpiling Enable = {enable}.");
        }

        void ToggleAntennas(bool enable)
        {
            foreach (var antenna in antennas)
            {
                if (antenna.CubeGrid == Me.CubeGrid)
                {
                    if (enable)
                        antenna.Enabled = enable;
                    else
                        antenna.Enabled = enable;
                }
            }
            //Echo($"Antenna Enable = {enable}.");
        }

        void ToggleAIMoveBehaviour(bool enable)
        {
            foreach (var flightMovementBlock in flightMovementBlocks)
            {
                if (flightMovementBlock.CubeGrid == Me.CubeGrid)
                {
                    if (enable)
                    {
                        flightMovementBlock.ApplyAction("ActivateBehavior_On");
                        flightMovementBlock.ApplyAction("DockingMode_Off");
                    }
                    else
                    {
                        flightMovementBlock.ApplyAction("ActivateBehavior_Off");
                        flightMovementBlock.ApplyAction("DockingMode_Off");
                    }
                }
            }
            //Echo($"Move Behaviour Enabled = {enable}.");
        }

        void ToggleBasicTaskBehaviour(bool enable)
        {
            foreach (var basicMissionBlock in basicMissionBlocks)
            {
                if (basicMissionBlock.CubeGrid == Me.CubeGrid)
                {
                    if (enable)
                        basicMissionBlock.ApplyAction("ActivateBehavior_On");
                    else
                        basicMissionBlock.ApplyAction("ActivateBehavior_Off");
                }
            }
            //Echo($"Basic Task Behaviour Enabled = {enable}.");
        }

        void ToggleOffensiveBlockkBehaviour(bool enable)
        {
            foreach (var offensiveCombatBlock in offensiveCombatBlocks)
            {
                if (offensiveCombatBlock.CubeGrid == Me.CubeGrid)
                {
                    if (enable)
                        offensiveCombatBlock.ApplyAction("ActivateBehavior_On");
                    else
                        offensiveCombatBlock.ApplyAction("ActivateBehavior_Off");

                }
            }
            //Echo($"Offensive Behaviour Enabled = {enable}.");
        }

        void ToggleMoveBlockCollisionAvoidance(bool enable)
        {
            // Get a list of all Flight Move Blocks
            List<IMyFlightMovementBlock> flightMovementBlocks = new List<IMyFlightMovementBlock>();
            GridTerminalSystem.GetBlocksOfType(flightMovementBlocks);

            foreach (var flightMovementBlock in flightMovementBlocks)
            {
                if (flightMovementBlock.CubeGrid == Me.CubeGrid)
                {
                    if (enable)
                    {
                        flightMovementBlock.CollisionAvoidance = true;
                    }
                    else
                    {
                        flightMovementBlock.CollisionAvoidance = false;
                    }
                }
            }
        }

        void FindAndStoreDroneShipController()
        {
            if (droneShipController == null)
            {
                // Search for the first cockpit on the grid
                List<IMyShipController> shipControllers = new List<IMyShipController>();
                GridTerminalSystem.GetBlocksOfType(shipControllers);

                foreach (IMyShipController controller in shipControllers)
                {
                    // Check if the controller is a cockpit (you can adjust this condition if needed)
                    if (controller is IMyCockpit && controller.CubeGrid == Me.CubeGrid)
                    {
                        // Cache the found cockpit and break out of the loop
                        droneShipController = controller;
                        //Echo("Ship controller found.");
                        return;  // Exit the function after finding the ship controller
                    }
                }
                // Handle the case where no suitable ship controller is found
                Echo("No suitable ship controller found.");
            }

        }

        void FindAndStoreDroneSpugProgrammableBlock()
        {
            if (spugProgrammableBlock == null)
            {
                List<IMyProgrammableBlock> programmableBlocks = new List<IMyProgrammableBlock>();
                GridTerminalSystem.GetBlocksOfType(programmableBlocks);

                foreach (IMyProgrammableBlock programmableBlock in programmableBlocks)
                {
                    if (programmableBlock.CustomName.Contains("SPUG") && programmableBlock.CubeGrid == Me.CubeGrid)
                    {
                        spugProgrammableBlock = programmableBlock;
                        //Echo("SPUG PB found.");
                        return;
                    }
                }
                Echo("No SPUG PB found.");
            }
        }

        void FindAndStoreDroneControlProgrammableBlock()
        {
            if (droneControlProgrammableBlock == null)
            {
                List<IMyProgrammableBlock> programmableBlocks = new List<IMyProgrammableBlock>();
                GridTerminalSystem.GetBlocksOfType(programmableBlocks);

                foreach (IMyProgrammableBlock programmableBlock in programmableBlocks)
                {
                    if (programmableBlock.CustomName.Contains("Control") && programmableBlock.CubeGrid == Me.CubeGrid)
                    {
                        droneControlProgrammableBlock = programmableBlock;
                        //Echo("SPUG PB found.");
                        return;
                    }
                }
                Echo("No Drone Control PB found.");
            }
        }

        void FindAndStoreDroneConnector()
        {
            if (droneConnector == null)
            {
                List<IMyShipConnector> connectors = new List<IMyShipConnector>();
                GridTerminalSystem.GetBlocksOfType(connectors);

                foreach (IMyShipConnector connector in connectors)
                {
                    if (connector.CubeGrid == Me.CubeGrid)
                    {
                        droneConnector = connector;
                        //Echo("My Connector found.");
                        return;
                    }
                }
                Echo("No My Connector found.");
            }
        }

        void FindAndStoreDroneAntenna()
        {
            if (droneAntenna == null)
            {
                List<IMyRadioAntenna> radioAntennas = new List<IMyRadioAntenna>();
                GridTerminalSystem.GetBlocksOfType(radioAntennas);

                foreach (IMyRadioAntenna radioAntenna in radioAntennas)
                {
                    if (radioAntenna.CubeGrid == Me.CubeGrid)
                    {
                        droneAntenna = radioAntenna;
                        return;
                    }
                }
                Echo("No Drone Antenna found.");
            }
        }

        void FindAndStoreDroneRemoteControl()
        {
            if (myRemoteControl == null)
            {
                List<IMyRemoteControl> remoteControls = new List<IMyRemoteControl>();
                GridTerminalSystem.GetBlocksOfType(remoteControls);

                foreach (IMyRemoteControl remoteControl in remoteControls)
                {
                    if (remoteControl.CubeGrid == Me.CubeGrid)
                    {
                        myRemoteControl = remoteControl;
                        //Echo("My Connector found.");
                        return;
                    }
                }
                Echo("No Remote Control found.");
            }
        }

        void FindAndStoreLCDPanel()
        {
            if (lcdPanel == null)
            {
                List<IMyTextPanel> lcdpanels = new List<IMyTextPanel>();
                GridTerminalSystem.GetBlocksOfType(lcdpanels, block => block.CubeGrid == Me.CubeGrid);
                lcdPanel = lcdpanels.Count > 0 ? lcdpanels[0] : null;
            }
        }

        IMyShipConnector SaveCarrierConnector()
        {
            if (droneConnector.Status == MyShipConnectorStatus.Connected || droneConnector.Status == MyShipConnectorStatus.Connectable)
            {
                carrier_connector = droneConnector.OtherConnector;
                lcdPanel.WriteText($"Carrier Connector:\n{droneConnector.OtherConnector.CustomName}\n", true);
                return carrier_connector;
            }
            else
            {
                lcdPanel.WriteText($"Not connected to another connector", true);
                return null;
            }
        }

        void CheckIfDroneHasArrivedAtDockingWaypoint()
        {
            if (myRemoteControl != null && spugProgrammableBlock != null && lcdPanel != null)
            {
                //Runtime.UpdateFrequency = UpdateFrequency.Update1;
                double distanceToWaypoint = (myRemoteControl.GetPosition() - dockWaypoint).Length();
                //lcdPanel.WriteText($"Docking\nDock Waypoint:\n {distanceToWaypoint:F2}\n", false);


                if (distanceToWaypoint < 5.0)
                {
                    lcdPanel.WriteText("Dock WP Reached\n", true);
                    //Echo("Waypoint reached!");
                    droneIsFlyingToDockingWaypoint = false;
                    myRemoteControl.ApplyAction("AutoPilot_Off");
                    spugProgrammableBlock.TryRun(carrier_connector.CustomName);
                    lcdPanel.WriteText($"SPUG Dock:\n{carrier_connector.CustomName}\n", true);
                    //lcdPanel.WriteText($"GPS:{Me.CubeGrid.CustomName}:{dockWaypoint.X}:{dockWaypoint.Y}:{dockWaypoint.Z}:#FF75C9F1", false);
                    //Echo($"SPUG PB run with command: {Me.CubeGrid.CustomName}.");
                    //Runtime.UpdateFrequency = UpdateFrequency.None;
                }
            }
            else
            {
                Echo("No remote control or SPUG programmable block found on the executing grid.");
            }
        }

        void FindAndStoreDroneOffensiveBlock()
        {
            if (droneOffensiveBlock == null)
            {
                List<IMyOffensiveCombatBlock> offensiveCombatBlocks = new List<IMyOffensiveCombatBlock>();
                GridTerminalSystem.GetBlocksOfType(offensiveCombatBlocks);

                foreach (IMyOffensiveCombatBlock offensiveCombatBlock in offensiveCombatBlocks)
                {
                    if (offensiveCombatBlock.CubeGrid == Me.CubeGrid)
                    {
                        droneOffensiveBlock = offensiveCombatBlock;
                        return;
                    }
                }
                Echo("No Offensive Block found.");
            }
        }

        void UpdateAntennaWithOffensiveBlockCondition()
        {
            if (droneOffensiveBlock != null) 
            {
                IMySearchEnemyComponent searchComponent = droneOffensiveBlock.SearchEnemyComponent;
                if (searchComponent != null)
                {
                    var foundEnemyId = searchComponent.FoundEnemyId;
                    if (foundEnemyId.HasValue)
                    {
                        updateAntennaWithAttacking = true;
                    }
                    else
                    {
                        updateAntennaWithScanning = true;
                    }
                }
            }
        }

        bool IsDroneForwardOfCarrierCenter(Vector3D dockingPos, Vector3D shipCenterPos, Vector3D shipForwardDir)
        {
            shipForwardDir.Normalize();  // Modifies shipForwardDir in place

            Vector3D dockingDir = dockingPos - shipCenterPos;
            double dotProduct = Vector3D.Dot(dockingDir, shipForwardDir);
            lcdPanel.WriteText($"Dot Product:\n {dotProduct:F2}\n", false);

            if (dotProduct >= 5.0)
                return true; // >= 0 means forward
            else
                return false;
        }

        bool IsCarrierGrid()
        {
            if (Me.CubeGrid.CustomName.Contains("Carrier"))
                return true;
            else
                return false;
        }

        void CheckConditionsAndUpdateAntenna()
        {
            if (updateAntennaWithLaunching)
            {
                UpdateAntennaStatus("Launching");
                updateAntennaWithLaunching = false;
            }
            if (updateAntennaWithDocking)
            {
                UpdateAntennaStatus("Docking");
                updateAntennaWithDocking = false;
            }
            if (updateAntennaWithAttacking)
            {
                UpdateAntennaStatus("Attacking");
                updateAntennaWithAttacking = false;
            }
            if (updateAntennaWithScanning)
            {
                UpdateAntennaStatus("Scanning");
                updateAntennaWithScanning = false;
            }
            if (updateAntennaWithDocked)
            {
                UpdateAntennaStatus("Docked");
                updateAntennaWithDocked = false;
            }
        }

        void UpdateAntennaStatus(string status)
        {
            if (droneAntenna != null)
            {
                string gridName = droneAntenna.CubeGrid.CustomName;
                string antennaName = $"Antenna - {gridName} - {status}";
                droneAntenna.CustomName = antennaName;
            }
        }

        private bool ShouldRunMain(string argument, UpdateType updateType)
        {
            return (updateType & (UpdateType.Trigger | UpdateType.Terminal)) > 0 ||
                   (updateType & (UpdateType.Mod)) > 0 ||
                   (updateType & (UpdateType.Script)) > 0;
        }

        void RenameGridBasedOnCarrierConnectorAndSuffixBlocks()
        {
            IMyShipConnector carrierConnector = SaveCarrierConnector();

            if (carrierConnector != null)
            {
                string carrierConnectorName = carrierConnector.CustomName;
                string gridName = GetNumberFromString(carrierConnectorName);

                if (!string.IsNullOrEmpty(gridName))
                {
                    RenameGrid(gridName);
                }
            }
        }

        string GetNumberFromString(string inputString)
        {
            // Assuming the input string contains a number
            string[] words = inputString.Split(' ');

            foreach (var word in words)
            {
                int number; // Declare the out variable before using it
                if (int.TryParse(word, out number))
                {
                    Echo($"Drone {number}");
                    return $"Drone {number}";
                }
            }

            Echo($"Unable to parse number from input string: {inputString}");
            return null; // Unable to parse number
        }

        void RenameGrid(string newGridName)
        {
            IMyCubeGrid currentGrid = droneConnector.CubeGrid;

            if (currentGrid != null)
            {
                currentGrid.CustomName = newGridName;
                Echo($"Grid renamed to {newGridName}");
                
            }
            else
            {
                lcdPanel.WriteText($"Unable to rename grid. Current grid is null.", true);
            }
        }
    }
}
