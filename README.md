# OvenDemo Client

OvenDemo Client is a FactoryTalk Optix application that represents a distributed oven station connected to the central OvenDemo_Server.

## Overview

The solution consists of two FactoryTalk Optix projects:

- **Server**: Provides centralized monitoring, recipe management, and client supervision.
- **Client**: Local oven application used by operators to control and monitor the process.

Clients communicate with the server through MQTT.

## Features

### Client

- Local oven operation and monitoring
- Recipe download and synchronization from the server
- MQTT communication with the central server
- Status reporting and diagnostics
- Integrated chat with the server application

## Architecture

```text
+-------------------+
|  OvenDemo_Server  |
|-------------------|
| Client Overview   |
| Recipe Management |
| Operator Chat     |
+---------+---------+
          ^
          | MQTT
          v
+-------------------+
|  OvenDemo_Client  |
|-------------------|
| Oven Operation    |
| Local Monitoring  |
| Recipe Execution  |
| Operator Chat     |
+-------------------+
```

## Purpose

This project demonstrates how FactoryTalk Optix can be used to implement a distributed oven control system, where multiple client applications receive centralized recipe management while maintaining local control capabilities and direct communication with the server through an integrated chat system.
``