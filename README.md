# OvenDemo Server

OvenDemo Server is a FactoryTalk Optix application that provides centralized management and monitoring for a distributed oven system.

## Overview

The solution consists of two FactoryTalk Optix projects:

- **Server**: Central application used to monitor all connected clients, manage recipes, and communicate with operators.
- **Clients**: Distributed applications that connect to the server via MQTT.

## Features

### Server

- Real-time overview of all connected clients
- Centralized recipe management
- Recipe distribution to client devices
- MQTT-based communication
- Integrated chat with connected clients
- Centralized supervision and diagnostics

### Clients

- MQTT connection to the central server
- Local oven control and monitoring
- Automatic synchronization of recipes from the server
- Bidirectional chat communication with operators

## Architecture

```text
+-------------------+
|  OvenDemo_Server  |
|-------------------|
| Client Overview   |
| Recipe Management |
| Chat Interface    |
+---------+---------+
          |
          | MQTT
          |
+---------+---------+---------+
|                   |         |
v                   v         v
Client 1        Client 2   Client N
```

## Purpose

This project demonstrates how FactoryTalk Optix can be used to build a centralized recipe management and monitoring system, allowing multiple distributed oven stations to communicate with a central server through MQTT. The solution also includes an integrated chat system that enables direct communication between operators and connected clients.
